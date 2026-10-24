namespace Discharge.Services
{
    public static class TextRules
    {
        private static readonly char[] Separators = { ' ', '　', '\t' };

        public static string Clean(string? s) => (s ?? "").Trim();

        /// <summary>空格（半形、全形都可以）分開的每個關鍵字，都要出現在其中一個欄位裡（不分大小寫）。沒有關鍵字就全部符合。</summary>
        public static bool Matches(string? query, params string?[] fields)
        {
            var keywords = (query ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            return keywords.All(k => fields.Any(f => f != null && f.Contains(k, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
