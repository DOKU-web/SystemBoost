// Rules for what the elevated helper accepts. Built and run by framegen/tests/run_all.sh.
#include "../src/validate.h"
#include <cstdio>

static int failed = 0;
#define CHECK(x) do { if (!(x)) { printf("FAIL line %d: %s\n", __LINE__, #x); failed++; } } while (0)

int main() {
    using namespace sb;

    CHECK(split("a\tb c\td").size() == 3);
    CHECK(split("a\tb c\td")[1] == "b c");
    CHECK(split("ping").size() == 1);

    CHECK(isHex("0123456789abcdef0123456789abcdef", 32));
    CHECK(!isHex("0123456789ABCDEF0123456789ABCDEF", 32));
    CHECK(!isHex("0123", 32));

    CHECK(isExeName("game.exe"));
    CHECK(isExeName("My Game 2.EXE"));
    CHECK(isExeName("\xEA\xB2\x8C\xEC\x9E\x84.exe"));   // Korean file name in UTF-8
    CHECK(!isExeName("game"));
    CHECK(!isExeName("game.dll"));
    CHECK(!isExeName(".exe"));
    CHECK(!isExeName("..\\..\\evil.exe"));
    CHECK(!isExeName("C:\\Windows\\notepad.exe"));
    CHECK(!isExeName("a/b.exe"));
    CHECK(!isExeName("a\tb.exe"));
    CHECK(!isExeName("lsass.exe"));
    CHECK(!isExeName("WinLogon.EXE"));
    CHECK(!isExeName("svchost.exe"));
    CHECK(!isExeName("systemboost.exe"));
    CHECK(!isExeName(std::string(200, 'a') + ".exe"));

    CHECK(isPriorityClass("3"));
    CHECK(isPriorityClass("2"));
    CHECK(!isPriorityClass("4"));   // realtime
    CHECK(!isPriorityClass("0"));
    CHECK(!isPriorityClass("33"));

    CHECK(isHagsMode("1") && isHagsMode("2"));
    CHECK(!isHagsMode("0") && !isHagsMode(""));

    CHECK(isWuName("PauseUpdatesExpiryTime"));
    CHECK(!isWuName("ActiveHoursStart"));
    CHECK(!isWuName(""));

    CHECK(isIsoTime("2026-11-05T14:00:00Z"));
    CHECK(!isIsoTime("2026-11-05 14:00:00"));
    CHECK(!isIsoTime("2026-11-05T14:00:00Z;"));
    CHECK(!isIsoTime("abcd-11-05T14:00:00Z"));

    CHECK(isFrameLimit("10") && isFrameLimit("30") && isFrameLimit("300"));
    CHECK(!isFrameLimit("9") && !isFrameLimit("0") && !isFrameLimit("-30") && !isFrameLimit("301") && !isFrameLimit("30.5") && !isFrameLimit(""));
    CHECK(isOldValue("none") && isOldValue("1"));
    CHECK(!isOldValue("") && !isOldValue("-1") && !isOldValue("12345678901"));

    CHECK(isInt("0", 0, 1) && isInt("-200", -200, 100));
    CHECK(!isInt("", 0, 1) && !isInt("-", -1, 1) && !isInt("+1", 0, 5) && !isInt("1 ", 0, 5) && !isInt("1.5", 0, 5));
    CHECK(!isInt("99999999999", 0, 2000000000) && !isInt("-5", 0, 5) && !isInt("6", 0, 5));

    CHECK(isCoreOffset("100") && isCoreOffset("-200") && !isCoreOffset("101") && !isCoreOffset("-201"));
    CHECK(isMemOffset("300") && isMemOffset("-500") && !isMemOffset("301") && !isMemOffset("-501"));
    CHECK(isTempLimit("85") && !isTempLimit("88") && !isTempLimit("39"));
    CHECK(isPowerWatts("170") && !isPowerWatts("0") && !isPowerWatts("1001"));

    CHECK(isPagefileLine("?:\\pagefile.sys"));
    CHECK(isPagefileLine("C:\\pagefile.sys"));
    CHECK(isPagefileLine("c:\\PAGEFILE.SYS 0 0"));
    CHECK(isPagefileLine("D:\\pagefile.sys 16384 32768"));
    CHECK(isPagefileLine("D:\\pagefile.sys 1024 1024"));
    CHECK(!isPagefileLine("?:\\pagefile.sys 0 0"));
    CHECK(!isPagefileLine("D:\\pagefile.sys 32768 16384"));   // initial over maximum
    CHECK(!isPagefileLine("D:\\pagefile.sys 0 1024"));
    CHECK(!isPagefileLine("D:\\pagefile.sys 1024"));
    CHECK(!isPagefileLine("D:\\pagefile.sys  1024 2048"));
    CHECK(!isPagefileLine("D:\\pagefile.sys 1024 2048 "));
    CHECK(!isPagefileLine("D:\\pagefile.sys -1 2048"));
    CHECK(!isPagefileLine("D:\\pagefile.sys 1024 999999999"));
    CHECK(!isPagefileLine("D:\\pagefile.sys 1024 16777217"));
    CHECK(!isPagefileLine("D:\\other.sys"));
    CHECK(!isPagefileLine("D:\\dir\\pagefile.sys"));
    CHECK(!isPagefileLine("1:\\pagefile.sys"));
    CHECK(!isPagefileLine("\\\\server\\pagefile.sys"));
    CHECK(!isPagefileLine("D:\\pagefile.sys\t1 2"));
    CHECK(!isPagefileLine(""));
    {
        std::vector<std::string> f = {"pf.set", "C:\\pagefile.sys 0 0", "D:\\pagefile.sys 1024 2048"};
        CHECK(isPagefileList(f, 1));
        f.push_back("c:\\pagefile.sys");
        CHECK(!isPagefileList(f, 1));   // C: twice
        CHECK(isPagefileList({"pf.set", "?:\\pagefile.sys"}, 1));
        CHECK(!isPagefileList({"pf.set", "?:\\pagefile.sys", "C:\\pagefile.sys"}, 1));
        CHECK(isPagefileList({"pf.set", "none"}, 1));
        CHECK(!isPagefileList({"pf.set", "none", "C:\\pagefile.sys"}, 1));
        CHECK(!isPagefileList({"pf.set"}, 1));
        CHECK(!isPagefileList({"pf.set", ""}, 1));
        std::vector<std::string> many = {"pf.set"};
        for (char d = 'A'; d <= 'Z'; d++) many.push_back(std::string(1, d) + ":\\pagefile.sys");
        CHECK(isPagefileList(many, 1));
        many.push_back("A:\\pagefile.sys");
        CHECK(!isPagefileList(many, 1));
    }

    printf(failed ?"helper validate: %d failed\n" : "helper validate: all passed\n", failed);
    return failed ? 1 : 0;
}
