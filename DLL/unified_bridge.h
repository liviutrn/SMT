#pragma once

class Vehicle;

void InitUnifiedBridge();
void UpdateUnifiedBridge(Vehicle* vehicle);
void ShutdownUnifiedBridge();
float NormalizeUnifiedPedalRaw(int rawValue, bool clutch, bool positiveDirection);
