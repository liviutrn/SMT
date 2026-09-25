from pathlib import Path
import sys

root = Path(sys.argv[1] if len(sys.argv) > 1 else '.')
p = root / 'src/layer/openxr_layer.cpp'
s = p.read_text(encoding='utf-8')
old_decl = '[[nodiscard]] XrDuration generated_application_period(const std::shared_ptr<SessionState>& state, XrDuration period) noexcept;'
new_decl = '''[[nodiscard]] XrDuration generated_application_period(\n    const std::shared_ptr<SessionState>& state,\n    XrDuration reported_period,\n    XrDuration stable_period = 0) noexcept;'''
if s.count(old_decl) != 1:
    raise RuntimeError(f'period forward declaration expected once, found {s.count(old_decl)}')
s = s.replace(old_decl, new_decl, 1)
old_def = '''    XrDuration reported_period,\n    XrDuration stable_period = 0) noexcept {'''
new_def = '''    XrDuration reported_period,\n    XrDuration stable_period) noexcept {'''
if s.count(old_def) != 1:
    raise RuntimeError(f'period definition signature expected once, found {s.count(old_def)}')
s = s.replace(old_def, new_def, 1)
p.write_text(s, encoding='utf-8', newline='\n')
print('4x period helper declaration/definition aligned successfully')
