#include "shared.h"
#include "config.h"
#include "game_data.h"
#include "input.h"
#include "unified_bridge.h"
#include <cctype>
#include <cstring>
#include <iterator>

namespace {
	constexpr DWORD MapSize = 2048;
	constexpr std::uint32_t Magic = 0x31555253; // SRU1
	HANDLE mapHandle = nullptr;
	std::uint8_t* mapView = nullptr;
	std::int32_t lastSequence = -1;
	bool lastStallRequest = false;
	int clutchMinimum = 0;
	int clutchMaximum = 65535;
	int throttleMinimum = 0;
	int throttleMaximum = 65535;
	bool clutchInvert = false;
	bool throttleInvert = false;

	const char* BindingActions[] = { "GEAR R", "GEAR N", "GEAR 1", "GEAR 2", "GEAR 3", "GEAR 4", "GEAR 5", "GEAR 6", "RANGE LOW", "GEAR L", "RANGE HIGH" };

	template<typename T> T Read(const size_t offset) {
		T value{};
		std::memcpy(&value, mapView + offset, sizeof(T));
		return value;
	}

	template<typename T> void Write(const size_t offset, const T value) {
		std::memcpy(mapView + offset, &value, sizeof(T));
	}

	std::int64_t UtcDotNetTicks() {
		FILETIME fileTime{};
		GetSystemTimeAsFileTime(&fileTime);
		ULARGE_INTEGER raw{};
		raw.LowPart = fileTime.dwLowDateTime;
		raw.HighPart = fileTime.dwHighDateTime;
		return static_cast<std::int64_t>(raw.QuadPart + 504911232000000000ULL);
	}

	std::string ReadString(const size_t offset, const size_t length) {
		const auto text = reinterpret_cast<const char*>(mapView + offset);
		return std::string(text, strnlen_s(text, length));
	}

	std::string DevicePrefix() {
		if (joystickList.empty()) return "";
		std::stringstream stream(joystickList.front()->vendor());
		std::string word, result;
		while (stream >> word) if (!word.empty()) result += static_cast<char>(std::toupper(static_cast<unsigned char>(word.front())));
		return result;
	}

	std::string AxisBinding(const std::string& friendly) {
		const auto prefix = DevicePrefix();
		if (prefix.empty()) return "NONE";
		int index = 0;
		if (sscanf_s(friendly.c_str(), "Axis %d", &index) == 1) return prefix + ".a." + std::to_string(index) + ".n";
		char component = 0;
		if (sscanf_s(friendly.c_str(), "Slider %c %d", &component, 1, &index) == 2)
			return prefix + ".s." + static_cast<char>(std::tolower(component)) + "." + std::to_string(index) + ".n";
		return friendly.empty() ? "NONE" : friendly;
	}

	int KeyCode(const char input) {
		const char key = static_cast<char>(std::toupper(static_cast<unsigned char>(input)));
		if (key >= '1' && key <= '9') return 2 + key - '1';
		if (key == '0') return 11;
		const std::string letters = "QWERTYUIOPASDFGHJKLZXCVBNM";
		const int codes[] = {16,17,18,19,20,21,22,23,24,25,30,31,32,33,34,35,36,37,38,44,45,46,47,48,49,50};
		const auto found = letters.find(key);
		return found == std::string::npos ? 0 : codes[found];
	}

	void ApplyBindings() {
		iniConfig["CONTROLLER"]["CLUTCH"] = AxisBinding(ReadString(192, 64));
		iniConfig["CONTROLLER"]["THROTTLE PEDAL"] = AxisBinding(ReadString(256, 64));
		for (size_t i = 0; i < std::size(BindingActions); ++i) {
			const auto value = ReadString(320 + i * 64, 64);
			if (value.rfind("Button ", 0) == 0) {
				int button = 0;
				if (sscanf_s(value.c_str(), "Button %d", &button) == 1 && !DevicePrefix().empty())
					iniConfig["CONTROLLER"][BindingActions[i]] = DevicePrefix() + ".b." + std::to_string(button);
			} else {
				const int key = value.size() == 1 ? KeyCode(value.front()) : 0;
				iniConfig["KEYBOARD"][BindingActions[i]] = key ? "Kb." + std::to_string(key) : value;
			}
		}
	}

	void ApplySettings() {
		const auto flags = Read<std::int32_t>(12);
		iniConfig["OPTIONS"]["DISABLE GAME SHIFTING"] = (flags & 1) != 0;
		iniConfig["OPTIONS"]["ANALOG CLUTCH"] = (flags & 2) != 0;
		iniConfig["OPTIONS"]["REQUIRE CLUTCH"] = (flags & 4) != 0;
		iniConfig["OPTIONS"]["PEDAL IDLE TAKEOFF"] = (flags & 2) != 0;
		iniConfig["CLUTCH"]["BITE START"] = std::clamp(Read<float>(16), 0.0f, 1.0f);
		iniConfig["CLUTCH"]["BITE END"] = std::clamp(Read<float>(20), 0.0f, 1.0f);
		iniConfig["CLUTCH"]["CURVE"] = std::clamp(Read<float>(24), 0.1f, 12.0f);
		iniConfig["CLUTCH"]["IDLE THROTTLE"] = std::clamp(Read<float>(28), 0.0f, 0.5f);
		clutchMinimum = Read<int>(56);
		clutchMaximum = Read<int>(60);
		clutchInvert = Read<std::uint8_t>(64) != 0;
		throttleMinimum = Read<int>(68);
		throttleMaximum = Read<int>(72);
		throttleInvert = Read<std::uint8_t>(76) != 0;
		ApplyBindings();
	}
}

void InitUnifiedBridge() {
	mapHandle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, L"Local\\SnowRunnerUnified.v1");
	if (!mapHandle) {
		mapHandle = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, MapSize, L"Local\\SnowRunnerUnified.v1");
	}
	if (mapHandle) mapView = static_cast<std::uint8_t*>(MapViewOfFile(mapHandle, FILE_MAP_ALL_ACCESS, 0, 0, MapSize));
	if (mapView) {
		Write<std::uint32_t>(0, Magic);
		Write<std::int32_t>(4, 1);
		LogMessage("SnowRunner Unified bridge ready.");
	}
}

void UpdateUnifiedBridge(Vehicle* vehicle) {
	if (!mapView) return;
	const auto sequence = Read<std::int32_t>(8);
	if (sequence != lastSequence) {
		lastSequence = sequence;
		ApplySettings();
	}

	const auto flags = Read<std::int32_t>(12);
	const bool stallRequest = (flags & 32) != 0;
	if (vehicle && stallRequest && !lastStallRequest) vehicle->StallCounter = 5.0f;
	lastStallRequest = stallRequest;

	Write<std::int32_t>(128, sequence);
	Write<std::int64_t>(136, UtcDotNetTicks());
	Write<std::int32_t>(144, vehicle && vehicle->TruckAction ? vehicle->TruckAction->Gear_1 : 0);
	Write<float>(148, std::clamp(clutchPedalAmount.load(), 0.0f, 1.0f));
	Write<float>(152, std::clamp(throttlePedalAmount.load(), 0.0f, 1.0f));
	Write<std::uint8_t>(156, vehicle ? 1 : 0);
}

void ShutdownUnifiedBridge() {
	if (mapView) UnmapViewOfFile(mapView);
	if (mapHandle) CloseHandle(mapHandle);
	mapView = nullptr;
	mapHandle = nullptr;
}

float NormalizeUnifiedPedalRaw(const int rawValue, const bool clutch, const bool positiveDirection) {
	const int minimum = clutch ? clutchMinimum : throttleMinimum;
	const int maximum = clutch ? clutchMaximum : throttleMaximum;
	const bool invert = clutch ? clutchInvert : throttleInvert;
	const float unsignedRaw = static_cast<float>(rawValue + 32768);
	const float range = static_cast<float>(std::max(maximum - minimum, 1));
	float normalized = std::clamp((unsignedRaw - minimum) / range, 0.0f, 1.0f);
	if (!positiveDirection) normalized = 1.0f - normalized;
	if (invert) normalized = 1.0f - normalized;
	return normalized;
}
