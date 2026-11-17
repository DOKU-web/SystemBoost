// A helper built without AMD's ADLX SDK (CMake option SB_ADLX=OFF, see third_party/adlx/README.md):
// amd.info says status "off" and every other AMD command answers err "off", so the app can tell users the
// feature is not in this build instead of asking them to update a driver that may be fine.
#include "amd.h"
#include "commands.h"

namespace {

class NoAdlx final : public amd::Driver {
public:
    amd::Status open() override { return amd::Status::Off; }
    std::string adlxVersion() override { return ""; }
    std::string driverVersion() override { return ""; }
    int gpuCount() override { return 0; }
    std::string gpuName(int) override { return ""; }
    bool integrated(int) override { return false; }
    amd::Res atFactory(int, bool&) override { return amd::Res::Unsupported; }
    amd::Res resetToFactory(int) override { return amd::Res::Unsupported; }
    amd::Res range(int, amd::Knob, amd::Range&) override { return amd::Res::Unsupported; }
    amd::Res get(int, amd::Knob, int&) override { return amd::Res::Unsupported; }
    amd::Res set(int, amd::Knob, int) override { return amd::Res::Unsupported; }
    bool autoSupported(int, amd::AutoMode) override { return false; }
    amd::Res autoStart(int, amd::AutoMode) override { return amd::Res::Unsupported; }
    bool autoDone() override { return false; }
    amd::Res toggleGet(int, amd::Toggle, bool&) override { return amd::Res::Unsupported; }
    amd::Res toggleSet(int, amd::Toggle, bool) override { return amd::Res::Unsupported; }
    amd::Res metrics(int, amd::Metrics&) override { return amd::Res::Unsupported; }
    amd::Res temperature(int, int&) override { return amd::Res::Unsupported; }
};

}  // namespace

amd::Driver& amdDriver() {
    static NoAdlx none;
    return none;
}
