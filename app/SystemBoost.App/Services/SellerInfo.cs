// The seller's details (전자상거래법 제13조 seller disclosure) as the server sends them: the "seller" object of
// GET /support/info, or the whole body of GET /seller. Both carry the same names (server/license-worker/src/support.js
// SELLER_FIELDS). No Avalonia here, so SystemBoost.Account.Tests compiles this file as it is.
// Nothing is filled in here: an empty or missing field stays empty and gets no row.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SystemBoost.App.Services;

public sealed record SupportSeller(string Name = "", string Rep = "", string Address = "", string BizNo = "", string MailOrderNo = "",
                                   string Email = "", string Phone = "", string Hours = "")
{
    public bool Empty => new[] { Name, Rep, Address, BizNo, MailOrderNo, Email, Phone, Hours }.All(string.IsNullOrWhiteSpace);

    /// The fields of a seller object; a field that is missing or not a string is empty. Not an object: all empty.
    public static SupportSeller Parse(JsonElement s)
    {
        string F(string name) =>
            s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return new SupportSeller(F("name"), F("rep"), F("address"), F("bizNo"), F("mailOrderNo"), F("email"), F("phone"), F("hours"));
    }

    /// Only the fields the seller filled in, trimmed, labelled as on 고객센터 and /help (the labels are Korean source
    /// strings with entries in Assets/lang.en.json). An empty field gets no row.
    public List<(string Label, string Value)> Rows()
    {
        var rows = new List<(string, string)>();
        void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value.Trim())); }
        Add("상호", Name); Add("대표자", Rep); Add("주소", Address); Add("사업자등록번호", BizNo);
        Add("통신판매업 신고번호", MailOrderNo); Add("메일", Email); Add("전화", Phone); Add("문의 받는 시간", Hours);
        return rows;
    }
}
