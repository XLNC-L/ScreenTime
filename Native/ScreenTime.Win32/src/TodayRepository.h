// TodayRepository.h — 今日页数据层（只读 SQLite + 内存聚合，对齐 WinUI 版口径）
//
// 查询口径与 ScreenTime.Core 完全一致：
//   - 总时长 / 前台 = 区间并集（重叠不重复计），后台 = 分钟取整后的差值
//   - 分类占比 / 排行 / 用途 = 前台∪音频并集；忽略应用过滤、合并链解析
//   - 时间线只取 foreground 片段，转为本地分钟
//   - 亮屏 = session_events 的 display_on/off 配对
// 数据库位置：%APPDATA%\ScreenTime\screentime.db

#pragma once

#include <windows.h>
#include "sqlite3.h"
#include <vector>
#include <string>
#include <utility>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>

// ---- 今日页聚合结果（纯数据，颜色由调用方映射）----
struct TodayResult
{
    bool ok = false;
    std::string error;

    double totalSec = 0.0;   // 今日总时长（并集）
    double fgSec = 0.0;      // 前台时长（并集）
    double displaySec = 0.0; // 亮屏时长

    struct CategorySlice { std::wstring name; double seconds; };
    struct RankItem      { std::wstring name; std::wstring exePath; double seconds; };
    struct PurposeItem   { std::wstring name; double seconds; };
    struct Slot          { double startMin = 0, endMin = 0; std::wstring app; std::wstring category; };

    std::vector<CategorySlice> categories;
    std::vector<RankItem> ranks;
    std::vector<PurposeItem> purposes;
    std::vector<Slot> slots;
};

// ---- 时间工具（ISO-8601 UTC ↔ epoch；本地分钟）----

inline double IsoToEpoch(const char* iso)
{
    // "yyyy-MM-ddTHH:mm:ss[.fff]Z"
    int y = 0, mo = 0, d = 0, h = 0, mi = 0, s = 0;
    if (sscanf_s(iso, "%d-%d-%dT%d:%d:%d", &y, &mo, &d, &h, &mi, &s) < 6)
        return 0;
    struct tm t{};
    t.tm_year = y - 1900; t.tm_mon = mo - 1; t.tm_mday = d;
    t.tm_hour = h; t.tm_min = mi; t.tm_sec = s;
    return (double)_mkgmtime(&t); // 按 UTC 解释
}

// 本地日期 0 点 → UTC epoch；次日 0 点 → UTC epoch
inline void LocalDayRange(time_t& from, time_t& to)
{
    SYSTEMTIME st;
    ::GetLocalTime(&st);
    struct tm t{};
    t.tm_year = st.wYear - 1900; t.tm_mon = st.wMonth - 1; t.tm_mday = st.wDay;
    t.tm_hour = 0; t.tm_min = 0; t.tm_sec = 0;
    from = mktime(&t); // 本地 0 点的 epoch（epoch 即 UTC 时刻）

    struct tm t2 = t;
    t2.tm_mday += 1;
    mktime(&t2); // 归一化到次日
    to = mktime(&t2);
}

// epoch（UTC 秒）→ 当日本地分钟（0-1440）
inline double EpochToLocalMinutes(double epoch)
{
    time_t e = (time_t)epoch;
    struct tm lt{};
    localtime_s(&lt, &e);
    return lt.tm_hour * 60.0 + lt.tm_min + lt.tm_sec / 60.0;
}

// 多个闭区间（epoch 秒）的并集总秒数
inline double UnionSeconds(std::vector<std::pair<double, double>> ivs)
{
    std::vector<std::pair<double, double>> valid;
    for (auto& p : ivs)
        if (p.second > p.first) valid.push_back(p);
    if (valid.empty()) return 0;
    std::sort(valid.begin(), valid.end());
    double total = 0;
    double cs = valid[0].first, ce = valid[0].second;
    for (size_t i = 1; i < valid.size(); ++i)
    {
        if (valid[i].first <= ce)
            ce = std::max(ce, valid[i].second);
        else
        {
            total += ce - cs;
            cs = valid[i].first; ce = valid[i].second;
        }
    }
    total += ce - cs;
    return total;
}

inline std::wstring Utf8ToWide(const char* s)
{
    if (!s || !*s) return L"";
    const int n = ::MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n - 1, L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

// ---- 用途推断（PurposeService.AutoFromCategory + Display）----
inline std::string AutoPurpose(const std::string& category)
{
    if (category == "开发与创作" || category == "办公与文档") return "工作学习";
    if (category == "游戏" || category == "影音与娱乐") return "消遣娱乐";
    if (category == "通讯与协作") return "社交通讯";
    if (category == "社交与购物" || category == "系统与工具" || category == "浏览与资讯")
        return "生活事务";
    return "";
}

inline std::string PurposeDisplay(const std::string& purpose)
{
    return purpose.empty() ? "未判定" : purpose;
}

// ---- 时长格式化（对齐 FormatDurationMinute / FormatDuration）----
inline std::wstring FormatDurationMinute(double seconds)
{
    const long long minutes = (long long)std::floor(seconds / 60.0);
    if (minutes < 1) return L"不足 1 分";
    const long long h = minutes / 60, m = minutes % 60;
    wchar_t buf[32];
    if (h > 0) swprintf_s(buf, L"%lld 小时 %lld 分", h, m);
    else       swprintf_s(buf, L"%lld 分", m);
    return buf;
}

inline std::wstring FormatDuration(double seconds)
{
    const long long total = (long long)std::floor(seconds);
    if (total < 1) return L"0 秒";
    const long long h = total / 3600, m = (total % 3600) / 60, s = total % 60;
    wchar_t buf[32];
    if (h >= 1)      swprintf_s(buf, L"%lld 小时 %lld 分", h, m);
    else if (m >= 1) swprintf_s(buf, L"%lld 分 %lld 秒", m, s);
    else             swprintf_s(buf, L"%lld 秒", s);
    return buf;
}

// ---- 今日数据加载 ----
class TodayRepository
{
public:
    TodayResult Load()
    {
        TodayResult r;

        wchar_t path[MAX_PATH]{};
        if (::GetEnvironmentVariableW(L"APPDATA", path, MAX_PATH) == 0)
        {
            r.error = "APPDATA 不可用";
            return r;
        }
        wcscat_s(path, L"\\ScreenTime\\screentime.db");

        sqlite3* db = nullptr;
        // sqlite3_open16 接受宽字符路径（UTF-16），避免窄串转换
        const int rc = sqlite3_open16(path, &db);
        if (rc != SQLITE_OK)
        {
            r.error = "无法打开数据库: " + std::string(sqlite3_errmsg(db));
            if (db) sqlite3_close(db);
            return r;
        }
        sqlite3_busy_timeout(db, 3000);

        // 今日 UTC 范围
        time_t fromEpoch = 0, toEpoch = 0;
        LocalDayRange(fromEpoch, toEpoch);
        const double from = (double)fromEpoch, to = (double)toEpoch;

        // 查询 SQL 工具
        sqlite3_stmt* st = nullptr;
        auto query = [&](const char* sql, int n) -> bool {
            return sqlite3_prepare_v2(db, sql, -1, &st, nullptr) == SQLITE_OK;
        };

        // 1) apps 信息
        struct AppInfo { std::string name, category, exePath; int ignored = 0, merged = 0; };
        std::vector<AppInfo> appsById(64); // 稀疏索引：id 一般不大
        std::vector<int> appIds;
        if (query("SELECT id, app_name, category, ignored, merged_into, exe_path FROM apps", 0))
        {
            while (sqlite3_step(st) == SQLITE_ROW)
            {
                const int id = sqlite3_column_int(st, 0);
                if (id >= (int)appsById.size()) appsById.resize(id + 1);
                appsById[id].name = (const char*)sqlite3_column_text(st, 1);
                appsById[id].category = (const char*)sqlite3_column_text(st, 2);
                appsById[id].ignored = sqlite3_column_int(st, 3);
                appsById[id].merged = sqlite3_column_int(st, 4);
                if (sqlite3_column_text(st, 5))
                    appsById[id].exePath = (const char*)sqlite3_column_text(st, 5);
                appIds.push_back(id);
            }
        }
        sqlite3_finalize(st); st = nullptr;

        auto resolveId = [&](int id) -> int {
            int cur = id;
            for (int depth = 0; depth < 5; ++depth)
            {
                if (cur < 0 || cur >= (int)appsById.size()) break;
                const int next = appsById[cur].merged;
                if (next == 0 || next == cur || next >= (int)appsById.size()) break;
                cur = next;
            }
            return cur;
        };

        // 2) 片段（一次查询，覆盖卡片/分类/排行/用途/时间线）
        //    桶：resolvedApp → (fg 区间, au 区间)；purpose → 区间；全部 → 区间
        struct AppBucket { std::vector<std::pair<double,double>> fg, au; };
        std::vector<AppBucket> appBuckets(appsById.size());
        std::vector<std::pair<double, double>> allIvs, fgIvs;
        std::vector<std::pair<std::string, std::vector<std::pair<double,double>>>> purposeBuckets;

        std::string segSql =
            "SELECT s.app_id, s.kind, s.start_ts, s.end_ts, s.purpose FROM usage_segments s "
            "WHERE s.end_ts > ?1 AND s.start_ts < ?2";
        if (sqlite3_prepare_v2(db, segSql.c_str(), -1, &st, nullptr) == SQLITE_OK)
        {
            char fromIso[32], toIso[32];
            FormatIsoUtc((time_t)from, fromIso, sizeof fromIso);
            FormatIsoUtc((time_t)to, toIso, sizeof toIso);
            sqlite3_bind_text(st, 1, fromIso, -1, SQLITE_TRANSIENT);
            sqlite3_bind_text(st, 2, toIso, -1, SQLITE_TRANSIENT);

            while (sqlite3_step(st) == SQLITE_ROW)
            {
                const int srcId = sqlite3_column_int(st, 0);
                if (srcId < 0 || srcId >= (int)appsById.size()) continue;
                const int resolved = resolveId(srcId);
                auto& target = appsById[resolved];
                if (appsById[srcId].ignored != 0 || target.ignored != 0) continue;

                const double s0 = IsoToEpoch((const char*)sqlite3_column_text(st, 2));
                const double e0 = IsoToEpoch((const char*)sqlite3_column_text(st, 3));
                double s = std::max(s0, from), e = std::min(e0, to);
                if (e <= s) continue;

                const bool fg = sqlite3_column_text(st, 1) &&
                                strcmp((const char*)sqlite3_column_text(st, 1), "foreground") == 0;
                auto& bucket = appBuckets[resolved];
                (fg ? bucket.fg : bucket.au).emplace_back(s, e);
                allIvs.emplace_back(s, e);
                if (fg) fgIvs.emplace_back(s, e);

                // 用途
                const char* explicitP = (const char*)sqlite3_column_text(st, 4);
                std::string purpose = (explicitP && *explicitP) ? explicitP : AutoPurpose(target.category);
                auto it = std::find_if(purposeBuckets.begin(), purposeBuckets.end(),
                    [&](const auto& pb) { return pb.first == purpose; });
                if (it == purposeBuckets.end())
                {
                    purposeBuckets.emplace_back(purpose, std::vector<std::pair<double,double>>{});
                    it = purposeBuckets.end() - 1;
                }
                it->second.emplace_back(s, e);

                // 时间线（foreground 只取，转本地分钟）
                if (fg)
                {
                    TodayResult::Slot slot;
                    slot.startMin = EpochToLocalMinutes(s);
                    slot.endMin = EpochToLocalMinutes(e);
                    slot.app = Utf8ToWide(target.name.c_str());
                    slot.category = Utf8ToWide(target.category.c_str());
                    r.slots.push_back(slot);
                }
            }
        }
        sqlite3_finalize(st); st = nullptr;

        // 3) 亮屏时长（session_events 配对）
        std::vector<std::pair<bool, double>> events;
        if (query("SELECT type, ts FROM session_events WHERE type IN ('display_on','display_off') AND ts <= ?1 ORDER BY ts", 1))
        {
            char toIso[32];
            FormatIsoUtc((time_t)to, toIso, sizeof toIso);
            sqlite3_bind_text(st, 1, toIso, -1, SQLITE_TRANSIENT);
            while (sqlite3_step(st) == SQLITE_ROW)
                events.emplace_back(strcmp((const char*)sqlite3_column_text(st, 0), "display_on") == 0,
                                    IsoToEpoch((const char*)sqlite3_column_text(st, 1)));
        }
        sqlite3_finalize(st); st = nullptr;
        sqlite3_close(db);

        // ---- 聚合 ----
        r.totalSec = UnionSeconds(allIvs);
        r.fgSec = UnionSeconds(fgIvs);
        r.displaySec = DisplayOnSeconds(events, from, to);

        // 分类占比（按 category 分组并集）
        {
            std::vector<std::pair<std::string, std::vector<std::pair<double,double>>>> catBuckets;
            for (const int id : appIds)
            {
                auto& b = appBuckets[id];
                if (b.fg.empty() && b.au.empty()) continue;
                std::vector<std::pair<double,double>> ivs = b.fg;
                ivs.insert(ivs.end(), b.au.begin(), b.au.end());
                const std::string& cat = appsById[id].category;
                auto it = std::find_if(catBuckets.begin(), catBuckets.end(),
                    [&](const auto& cb) { return cb.first == cat; });
                if (it == catBuckets.end())
                {
                    catBuckets.emplace_back(cat, std::vector<std::pair<double,double>>{});
                    it = catBuckets.end() - 1;
                }
                it->second.insert(it->second.end(), ivs.begin(), ivs.end());
            }
            for (auto& cb : catBuckets)
            {
                const double sec = UnionSeconds(cb.second);
                if (sec > 0) r.categories.push_back({ Utf8ToWide(cb.first.c_str()), sec });
            }
            std::sort(r.categories.begin(), r.categories.end(),
                [](const auto& a, const auto& b) { return a.seconds > b.seconds; });
        }

        // 排行（按应用并集，降序）
        for (const int id : appIds)
        {
            auto& b = appBuckets[id];
            if (b.fg.empty() && b.au.empty()) continue;
            std::vector<std::pair<double,double>> ivs = b.fg;
            ivs.insert(ivs.end(), b.au.begin(), b.au.end());
            const double sec = UnionSeconds(ivs);
            if (sec > 0)
                r.ranks.push_back({ Utf8ToWide(appsById[id].name.c_str()),
                                    Utf8ToWide(appsById[id].exePath.c_str()), sec });
        }
        std::sort(r.ranks.begin(), r.ranks.end(),
            [](const auto& a, const auto& b) { return a.seconds > b.seconds; });

        // 用途分布（并集，降序）
        for (auto& pb : purposeBuckets)
        {
            const double sec = UnionSeconds(pb.second);
            if (sec > 0)
                r.purposes.push_back({ Utf8ToWide(PurposeDisplay(pb.first).c_str()), sec });
        }
        std::sort(r.purposes.begin(), r.purposes.end(),
            [](const auto& a, const auto& b) { return a.seconds > b.seconds; });

        r.ok = true;
        return r;
    }

private:
    static void FormatIsoUtc(time_t t, char* out, size_t n)
    {
        struct tm u{};
        gmtime_s(&u, &t);
        snprintf(out, n, "%04d-%02d-%02dT%02d:%02d:%02d.000Z",
                 u.tm_year + 1900, u.tm_mon + 1, u.tm_mday,
                 u.tm_hour, u.tm_min, u.tm_sec);
    }

    // 亮屏秒数：display_on/off 配对，与查询范围求交；
    // 关键：区间上限截到现在（对齐 C# 版），否则最后一段"亮"会算到今晚 24 点
    static double DisplayOnSeconds(const std::vector<std::pair<bool, double>>& events,
                                   double from, double to)
    {
        const double now = (double)::time(nullptr);
        if (to > now) to = now;
        if (to <= from) return 0;
        double total = 0;
        bool on = false;
        double cursor = 0;
        for (const auto& [isOn, ts] : events)
        {
            if (on && ts > from)
            {
                const double start = cursor > from ? cursor : from;
                const double end = ts < to ? ts : to;
                if (end > start) total += end - start;
            }
            on = isOn;
            cursor = ts;
        }
        if (on)
        {
            const double start = cursor > from ? cursor : from;
            if (to > start) total += to - start;
        }
        return total;
    }
};
