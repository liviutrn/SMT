from pathlib import Path
import sys

ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else '.')


def read(rel):
    return (ROOT / rel).read_text(encoding='utf-8')


def write(rel, text):
    (ROOT / rel).write_text(text, encoding='utf-8', newline='\n')


def rep(text, old, new, label, count=1):
    found = text.count(old)
    if found != count:
        raise RuntimeError(f'{label}: expected {count}, found {found}')
    return text.replace(old, new, count)


def extract_balanced_function(text, start_marker):
    start = text.find(start_marker)
    if start < 0:
        raise RuntimeError(f'function marker not found: {start_marker}')
    brace = text.find('{', start)
    if brace < 0:
        raise RuntimeError('opening brace not found')
    depth = 0
    in_string = False
    escaped = False
    i = brace
    while i < len(text):
        ch = text[i]
        if in_string:
            if escaped:
                escaped = False
            elif ch == '\\':
                escaped = True
            elif ch == '"':
                in_string = False
        else:
            if ch == '"':
                in_string = True
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return start, i + 1, text[start:i + 1]
        i += 1
    raise RuntimeError(f'unterminated function: {start_marker}')

# ---------------------------------------------------------------------------
# 1. D3D11 batched publication: publish the real image once and all three
#    synthetic images behind one D3D12->D3D11 handoff/flush.
# ---------------------------------------------------------------------------
hpp_path = 'include/xrfg/d3d11_d3d12_interop.hpp'
hpp = read(hpp_path)
old_decl = '''    [[nodiscard]] HRESULT publish(\n        std::uint32_t current_destination_index,\n        std::optional<std::uint32_t> synthetic_destination_index) noexcept;\n'''
new_decl = old_decl + '''\n    // 4x path: publish the current image once together with all synthetic\n    // destinations behind a single cross-API synchronization/flush.\n    [[nodiscard]] HRESULT publish_many(\n        std::uint32_t current_destination_index,\n        std::span<const std::uint32_t> synthetic_destination_indices) noexcept;\n'''
hpp = rep(hpp, old_decl, new_decl, 'publish_many declaration')
write(hpp_path, hpp)

cpp_path = 'src/d3d11/d3d11_d3d12_interop.cpp'
cpp = read(cpp_path)
impl_marker = '    [[nodiscard]] HRESULT wait_for_idle() noexcept {'
impl_publish_many = r'''    [[nodiscard]] HRESULT publish_many(
        std::uint32_t current_destination_index,
        std::span<const std::uint32_t> synthetic_destination_indices) noexcept {
        if (!enabled ||
            current_destination_index >= current_destination_images.size() ||
            current_destination_index >= shared_current_destinations.size()) {
            return E_INVALIDARG;
        }
        for (const std::uint32_t index : synthetic_destination_indices) {
            if (index >= synthetic_destination_images.size() ||
                index >= shared_synthetic_destinations.size()) {
                return E_INVALIDARG;
            }
        }

        std::uint64_t ready_value = 0;
        HRESULT result = allocate_fence_value(&ready_value);
        if (FAILED(result)) return result;
        result = d3d12_queue->Signal(d3d12_fence.Get(), ready_value);
        if (FAILED(result)) {
            enabled = false;
            return result;
        }
        result = d3d11_context4->Wait(d3d11_fence.Get(), ready_value);
        if (FAILED(result)) {
            enabled = false;
            return result;
        }

        copy_mip_zero(
            d3d11_context.Get(),
            current_destination_images[current_destination_index].Get(),
            shared_current_destinations[current_destination_index].d3d11.Get(),
            source_description.ArraySize,
            1,
            [&]() noexcept {
                D3D11_TEXTURE2D_DESC description{};
                current_destination_images[current_destination_index]->GetDesc(
                    &description);
                return description.MipLevels;
            }());

        for (const std::uint32_t index : synthetic_destination_indices) {
            copy_mip_zero(
                d3d11_context.Get(),
                synthetic_destination_images[index].Get(),
                shared_synthetic_destinations[index].d3d11.Get(),
                source_description.ArraySize,
                1,
                [&]() noexcept {
                    D3D11_TEXTURE2D_DESC description{};
                    synthetic_destination_images[index]->GetDesc(&description);
                    return description.MipLevels;
                }());
        }

        std::uint64_t complete_value = 0;
        result = allocate_fence_value(&complete_value);
        if (FAILED(result)) return result;
        result = d3d11_context4->Signal(d3d11_fence.Get(), complete_value);
        if (FAILED(result)) {
            enabled = false;
            return result;
        }
        d3d11_context4->Flush1(D3D11_CONTEXT_TYPE_ALL, nullptr);
        last_d3d11_access_value = complete_value;
        last_fence_value = complete_value;
        return S_OK;
    }

'''
if impl_marker not in cpp:
    raise RuntimeError('Impl wait_for_idle marker not found')
cpp = cpp.replace(impl_marker, impl_publish_many + impl_marker, 1)

wrapper_marker = 'HRESULT D3D11D3D12SwapchainInterop::wait_for_idle() noexcept {'
wrapper_publish_many = r'''HRESULT D3D11D3D12SwapchainInterop::publish_many(
    std::uint32_t current_destination_index,
    std::span<const std::uint32_t> synthetic_destination_indices) noexcept {
    try {
        std::scoped_lock lock(mutex_);
        return impl_ == nullptr
            ? E_UNEXPECTED
            : impl_->publish_many(
                  current_destination_index,
                  synthetic_destination_indices);
    } catch (...) {
        return E_FAIL;
    }
}

'''
if wrapper_marker not in cpp:
    raise RuntimeError('wrapper wait_for_idle marker not found')
cpp = cpp.replace(wrapper_marker, wrapper_publish_many + wrapper_marker, 1)
write(cpp_path, cpp)

# ---------------------------------------------------------------------------
# 2. Layer robustness fixes.
# ---------------------------------------------------------------------------
layer_path = 'src/layer/openxr_layer.cpp'
layer = read(layer_path)

# Use the presenter's stable physical period for the 4x virtual application
# cadence. SteamVR may temporarily report 2x/3x/etc periods when late; feeding
# those directly back into the application's virtual period creates a feedback
# loop (30 -> 15 -> 7.5 fps, etc.).
old_period_fn = '''[[nodiscard]] XrDuration generated_application_period(const std::shared_ptr<SessionState>& state, XrDuration period) noexcept { return multiplied_display_period(period, state && state->optical_flow_backend == xrfg::D3D12OpticalFlowBackend::nvidia ? 4U : 2U); }'''
new_period_fn = '''[[nodiscard]] XrDuration generated_application_period(\n    const std::shared_ptr<SessionState>& state,\n    XrDuration reported_period,\n    XrDuration stable_period = 0) noexcept {\n    const XrDuration physical_period = stable_period > 0\n        ? stable_period\n        : reported_period;\n    return multiplied_display_period(\n        physical_period,\n        state && state->optical_flow_backend ==\n                         xrfg::D3D12OpticalFlowBackend::nvidia\n            ? 4U\n            : 2U);\n}'''
layer = rep(layer, old_period_fn, new_period_fn, 'stable period helper')
old_continuous = 'generated_application_period(state, state->presenter_frame_state.predictedDisplayPeriod)'
new_continuous = 'generated_application_period(state, state->presenter_frame_state.predictedDisplayPeriod, state->presenter_display_period)'
layer = rep(layer, old_continuous, new_continuous, 'continuous stable period')

# If an unusual runtime gives the additional synthetic swapchains a different
# image count, release everything already created before falling back.
old_mismatch = '''        for (const CreatedPrivateSwapchain& extra : additional_synthetic) {\n            if (extra.d3d12_resources.size() != synthetic.d3d12_resources.size()) return nullptr;\n            all_synthetic_d3d12.insert(all_synthetic_d3d12.end(), extra.d3d12_resources.begin(), extra.d3d12_resources.end());\n        }'''
new_mismatch = '''        for (CreatedPrivateSwapchain& extra : additional_synthetic) {\n            if (extra.d3d12_resources.size() != synthetic.d3d12_resources.size()) {\n                if (failure_reason != nullptr)\n                    *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed;\n                if (synthetic.state.handle != XR_NULL_HANDLE) {\n                    static_cast<void>(dispatch->destroy_swapchain(synthetic.state.handle));\n                    synthetic.state.handle = XR_NULL_HANDLE;\n                }\n                for (CreatedPrivateSwapchain& created : additional_synthetic) {\n                    if (created.state.handle != XR_NULL_HANDLE) {\n                        static_cast<void>(dispatch->destroy_swapchain(created.state.handle));\n                        created.state.handle = XR_NULL_HANDLE;\n                    }\n                }\n                destroy_current_ring(dispatch, &current);\n                return nullptr;\n            }\n            all_synthetic_d3d12.insert(\n                all_synthetic_d3d12.end(),\n                extra.d3d12_resources.begin(),\n                extra.d3d12_resources.end());\n        }'''
layer = rep(layer, old_mismatch, new_mismatch, 'd3d12 mismatch cleanup')

# Batch D3D11 publication in the quad path. This removes two redundant copies
# of the real frame and two redundant signal/wait/Flush1 cycles per source frame.
old_publish = '''                HRESULT publish_result = S_OK;\n                if (!request_pair) publish_result = generation->d3d11_interop->publish(current_destination_index, std::nullopt);\n                else { const std::size_t publish_count = request_quad ? 3U : 1U; for (std::size_t i = 0; i < publish_count && SUCCEEDED(publish_result); ++i) publish_result = generation->d3d11_interop->publish(current_destination_index, synthetic_destination_indices[i]); }'''
new_publish = '''                HRESULT publish_result = S_OK;\n                if (!request_pair) {\n                    publish_result = generation->d3d11_interop->publish(\n                        current_destination_index, std::nullopt);\n                } else if (request_quad) {\n                    publish_result = generation->d3d11_interop->publish_many(\n                        current_destination_index,\n                        std::span<const std::uint32_t>(\n                            synthetic_destination_indices.data(),\n                            synthetic_destination_indices.size()));\n                } else {\n                    publish_result = generation->d3d11_interop->publish(\n                        current_destination_index, synthetic_destination_indices[0]);\n                }'''
layer = rep(layer, old_publish, new_publish, 'batched D3D11 publish')

# Make quad enqueue transactional with respect to allocation: allocate all four
# PresenterSubmission objects first, then mutate the queue while holding the
# lock. An OOM can no longer leave a partial temporal sequence unaccounted.
start, end, _ = extract_balanced_function(layer, '[[nodiscard]] XrResult enqueue_presenter_quad(')
new_enqueue = r'''[[nodiscard]] XrResult enqueue_presenter_quad(
    const std::shared_ptr<SessionState>& state,
    std::shared_ptr<GeneratedFrameEndInfo> first,
    std::shared_ptr<GeneratedFrameEndInfo> second,
    std::shared_ptr<GeneratedFrameEndInfo> third,
    std::shared_ptr<GeneratedFrameEndInfo> current) noexcept {
    try {
        std::array<std::shared_ptr<GeneratedFrameEndInfo>, 4> frames{
            std::move(first), std::move(second), std::move(third), std::move(current)};
        std::array<std::shared_ptr<PresenterSubmission>, 4> requests{};
        for (std::size_t i = 0; i < requests.size(); ++i) {
            requests[i] = std::make_shared<PresenterSubmission>();
            requests[i]->owned_frame = std::move(frames[i]);
        }

        std::scoped_lock lock(state->presenter_mutex);
        if (!state->presenter_active || state->presenter_stop_requested ||
            XR_FAILED(state->presenter_failure)) {
            return XR_FAILED(state->presenter_failure)
                ? state->presenter_failure
                : XR_ERROR_SESSION_NOT_RUNNING;
        }
        for (auto& request : requests) {
            request->sequence = state->next_presenter_sequence++;
            state->presenter_submissions.push_back(std::move(request));
        }
        state->outstanding_presenter_submissions += requests.size();
        state->presenter_condition.notify_all();
        return XR_SUCCESS;
    } catch (...) {
        return XR_ERROR_OUT_OF_MEMORY;
    }
}'''
layer = layer[:start] + new_enqueue + layer[end:]

write(layer_path, layer)
print('OFXR 4x audit fixes applied successfully')
