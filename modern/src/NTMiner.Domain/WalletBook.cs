using System.Text.RegularExpressions;

namespace NTMiner.Modern;

/// <summary>
/// 钱包地址列表的维护规则。只做结构校验与去重，不做 Bech32 校验和验证——
/// 那需要完整实现 Bech32 解码，超出当前范围，界面上会明确提示用户自行核对。
/// </summary>
public static class WalletBook
{
    // 与 MiningOptions.Validate 使用同一条结构规则，避免两处漂移。
    private static readonly Regex AddressPattern = new(@"^prl1[a-z0-9]{20,120}$", RegexOptions.CultureInvariant);

    /// <summary>结构校验。返回 null 表示可用，否则是给用户看的原因。</summary>
    public static string? Validate(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "请填写收款地址。";
        if (address.Any(char.IsWhiteSpace)) return "收款地址不能包含空格。";
        return AddressPattern.IsMatch(address.Trim()) ? null : "请填写 prl1 开头的 PRL 收款地址（不含矿机名）。";
    }

    /// <summary>
    /// 加入一个地址：去重后追加。已存在时原样返回，避免列表里出现重复项。
    /// 无效地址不加入，返回原列表与原因。
    /// </summary>
    public static (string[] Wallets, string? Error) Add(IEnumerable<string>? existing, string? address)
    {
        var list = (existing ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        if (Validate(address) is { } error) return (list.ToArray(), error);
        var trimmed = address!.Trim();
        if (list.Any(w => w.Equals(trimmed, StringComparison.Ordinal))) return (list.ToArray(), null);
        list.Add(trimmed);
        return (list.ToArray(), null);
    }

    /// <summary>移除指定地址；不存在时返回原列表。</summary>
    public static string[] Remove(IEnumerable<string>? existing, string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (existing ?? []).ToArray();
        return (existing ?? []).Where(w => !w.Equals(address.Trim(), StringComparison.Ordinal)).ToArray();
    }

    /// <summary>
    /// 让当前使用的地址与列表保持一致：若当前地址不在列表中，将其补入；
    /// 若列表非空而当前地址为空，取第一个。返回整理后的两者。
    /// </summary>
    public static (string[] Wallets, string Current) Reconcile(IEnumerable<string>? existing, string? current)
    {
        var list = (existing ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal).ToList();
        var trimmed = current?.Trim() ?? "";
        if (trimmed.Length > 0 && !list.Contains(trimmed, StringComparer.Ordinal)) list.Insert(0, trimmed);
        if (trimmed.Length == 0 && list.Count > 0) trimmed = list[0];
        return (list.ToArray(), trimmed);
    }

    /// <summary>界面展示用：地址过长时保留首尾，中间省略。</summary>
    public static string Shorten(string? address, int head = 12, int tail = 6)
    {
        if (string.IsNullOrWhiteSpace(address)) return "（未填写）";
        var value = address.Trim();
        return value.Length <= head + tail + 3 ? value : $"{value[..head]}…{value[^tail..]}";
    }
}
