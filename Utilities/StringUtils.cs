namespace Emqo.KookBot_Unturned.Utilities
{
    internal static class StringUtils
    {
        /// <summary>
        /// 清理富文本标签，防止注入攻击。
        /// 将半角特殊字符替换为全角字符。
        /// </summary>
        public static string SanitizeRichText(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            return input
                .Replace("<", "＜")
                .Replace(">", "＞")
                .Replace("[", "［")
                .Replace("]", "］")
                .Replace("\\", "＼");
        }
    }
}
