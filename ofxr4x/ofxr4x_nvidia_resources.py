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


path = 'src/layer/openxr_layer.cpp'
s = read(path)

# The original midpoint path needs only one synthetic swapchain. Extra swapchains
# exist solely to keep three simultaneously queued 4x outputs alive. Allocate
# them only when the session selected NVIDIA, which is the only backend that
# can request submit_quad().
old_decl = '''    CreatedPrivateSwapchain synthetic;\n    std::array<CreatedPrivateSwapchain, 2> additional_synthetic{};\n    if (failure_reason != nullptr) {'''
new_decl = '''    CreatedPrivateSwapchain synthetic;\n    std::array<CreatedPrivateSwapchain, 2> additional_synthetic{};\n    const bool needs_quad_resources =\n        state->session->optical_flow_backend ==\n        xrfg::D3D12OpticalFlowBackend::nvidia;\n    if (failure_reason != nullptr) {'''
s = rep(s, old_decl, new_decl, 'quad resource gate declarations', count=2)

old_d3d12_create = '''        for (CreatedPrivateSwapchain& extra : additional_synthetic) {\n            if (!create_private_swapchain(state, private_info, &extra)) {\n                if (failure_reason != nullptr) *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed;\n                if (synthetic.state.handle != XR_NULL_HANDLE) { static_cast<void>(dispatch->destroy_swapchain(synthetic.state.handle)); synthetic.state.handle = XR_NULL_HANDLE; }\n                for (CreatedPrivateSwapchain& created : additional_synthetic) if (created.state.handle != XR_NULL_HANDLE) { static_cast<void>(dispatch->destroy_swapchain(created.state.handle)); created.state.handle = XR_NULL_HANDLE; }\n                destroy_current_ring(dispatch, &current); return nullptr;\n            }\n        }'''
new_d3d12_create = '''        if (needs_quad_resources) {\n            for (CreatedPrivateSwapchain& extra : additional_synthetic) {\n                if (!create_private_swapchain(state, private_info, &extra)) {\n                    if (failure_reason != nullptr)\n                        *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed;\n                    if (synthetic.state.handle != XR_NULL_HANDLE) {\n                        static_cast<void>(dispatch->destroy_swapchain(synthetic.state.handle));\n                        synthetic.state.handle = XR_NULL_HANDLE;\n                    }\n                    for (CreatedPrivateSwapchain& created : additional_synthetic) {\n                        if (created.state.handle != XR_NULL_HANDLE) {\n                            static_cast<void>(dispatch->destroy_swapchain(created.state.handle));\n                            created.state.handle = XR_NULL_HANDLE;\n                        }\n                    }\n                    destroy_current_ring(dispatch, &current);\n                    return nullptr;\n                }\n            }\n        }'''
s = rep(s, old_d3d12_create, new_d3d12_create, 'd3d12 conditional extra creation')

old_d3d12_flatten = '''        for (CreatedPrivateSwapchain& extra : additional_synthetic) {\n            if (extra.d3d12_resources.size() != synthetic.d3d12_resources.size()) {\n                if (failure_reason != nullptr)\n                    *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed;\n                if (synthetic.state.handle != XR_NULL_HANDLE) {\n                    static_cast<void>(dispatch->destroy_swapchain(synthetic.state.handle));\n                    synthetic.state.handle = XR_NULL_HANDLE;\n                }\n                for (CreatedPrivateSwapchain& created : additional_synthetic) {\n                    if (created.state.handle != XR_NULL_HANDLE) {\n                        static_cast<void>(dispatch->destroy_swapchain(created.state.handle));\n                        created.state.handle = XR_NULL_HANDLE;\n                    }\n                }\n                destroy_current_ring(dispatch, &current);\n                return nullptr;\n            }\n            all_synthetic_d3d12.insert(\n                all_synthetic_d3d12.end(),\n                extra.d3d12_resources.begin(),\n                extra.d3d12_resources.end());\n        }'''
new_d3d12_flatten = '''        if (needs_quad_resources) {\n            for (CreatedPrivateSwapchain& extra : additional_synthetic) {\n                if (extra.d3d12_resources.size() != synthetic.d3d12_resources.size()) {\n                    if (failure_reason != nullptr)\n                        *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed;\n                    if (synthetic.state.handle != XR_NULL_HANDLE) {\n                        static_cast<void>(dispatch->destroy_swapchain(synthetic.state.handle));\n                        synthetic.state.handle = XR_NULL_HANDLE;\n                    }\n                    for (CreatedPrivateSwapchain& created : additional_synthetic) {\n                        if (created.state.handle != XR_NULL_HANDLE) {\n                            static_cast<void>(dispatch->destroy_swapchain(created.state.handle));\n                            created.state.handle = XR_NULL_HANDLE;\n                        }\n                    }\n                    destroy_current_ring(dispatch, &current);\n                    return nullptr;\n                }\n                all_synthetic_d3d12.insert(\n                    all_synthetic_d3d12.end(),\n                    extra.d3d12_resources.begin(),\n                    extra.d3d12_resources.end());\n            }\n        }'''
s = rep(s, old_d3d12_flatten, new_d3d12_flatten, 'd3d12 conditional flatten')

old_d3d11_create = '''        for (CreatedPrivateSwapchain& extra : additional_synthetic) if (!create_private_swapchain(state, private_info, &extra)) { if (failure_reason != nullptr) *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed; destroy_private(); return nullptr; }'''
new_d3d11_create = '''        if (needs_quad_resources) {\n            for (CreatedPrivateSwapchain& extra : additional_synthetic) {\n                if (!create_private_swapchain(state, private_info, &extra)) {\n                    if (failure_reason != nullptr)\n                        *failure_reason = SwapchainEligibilityReason::synthetic_private_swapchain_failed;\n                    destroy_private();\n                    return nullptr;\n                }\n            }\n        }'''
s = rep(s, old_d3d11_create, new_d3d11_create, 'd3d11 conditional extra creation')

old_d3d11_flatten = '''        for (const CreatedPrivateSwapchain& extra : additional_synthetic) { if (extra.d3d11_resources.size() != synthetic.d3d11_resources.size()) { destroy_private(); return nullptr; } all_synthetic_d3d11.insert(all_synthetic_d3d11.end(), extra.d3d11_resources.begin(), extra.d3d11_resources.end()); }'''
new_d3d11_flatten = '''        if (needs_quad_resources) {\n            for (const CreatedPrivateSwapchain& extra : additional_synthetic) {\n                if (extra.d3d11_resources.size() != synthetic.d3d11_resources.size()) {\n                    destroy_private();\n                    return nullptr;\n                }\n                all_synthetic_d3d11.insert(\n                    all_synthetic_d3d11.end(),\n                    extra.d3d11_resources.begin(),\n                    extra.d3d11_resources.end());\n            }\n        }'''
s = rep(s, old_d3d11_flatten, new_d3d11_flatten, 'd3d11 conditional flatten')

write(path, s)
print('NVIDIA-only 4x synthetic resource allocation applied successfully')
