using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DivineDragon
{
    /// <summary>
    /// Decodes AssetRipper-style <c>\uXXXX</c> escape sequences inside double-quoted YAML scalars
    /// back into raw UTF-8 characters. Unity itself emits non-ASCII in plain style, so left
    /// untouched these escapes show up in the inspector as literal "選..." instead of the
    /// intended Japanese (or any other non-ASCII) text.
    /// </summary>
    public static class YamlStringNormalizer
    {
        // Match `key: "..."` lines. The value is captured between the quotes.
        // Anchored to MULTILINE so we operate one line at a time.
        // Group 1 = leading "key: " up to the opening quote.
        // Group 2 = the raw quoted body (no surrounding quotes).
        // Group 3 = trailing chars after the closing quote (usually nothing, sometimes a comment).
        private static readonly Regex QuotedScalarLineRegex = new Regex(
            @"^(?<lead>[^""\n]*?:\s*"")(?<body>(?:\\.|[^""\\])*)(?<tail>"".*)$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // Match \uXXXX inside a YAML double-quoted string body. We need exactly four hex digits
        // because YAML's `\u` escape is fixed-width. \U (8 hex) is allowed in YAML too; included
        // for completeness so the normalizer doesn't silently leave them behind.
        private static readonly Regex UnicodeEscapeRegex = new Regex(
            @"\\u(?<hex>[0-9a-fA-F]{4})|\\U(?<hex8>[0-9a-fA-F]{8})",
            RegexOptions.Compiled);

        /// <summary>
        /// Reads the YAML file, rewrites any \uXXXX escapes in double-quoted scalars to raw chars,
        /// and writes the file back only if anything changed.
        /// </summary>
        /// <returns><c>true</c> if the file was modified.</returns>
        public static bool NormalizeFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"YamlStringNormalizer: failed to read {filePath}: {ex.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(content) || content.IndexOf("\\u", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            string normalized = Normalize(content);
            if (ReferenceEquals(normalized, content) || normalized == content)
            {
                return false;
            }

            try
            {
                File.WriteAllText(filePath, normalized, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"YamlStringNormalizer: failed to write {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns <paramref name="content"/> with \uXXXX / \UXXXXXXXX escapes inside
        /// double-quoted YAML scalars decoded to raw characters. All other content is
        /// preserved verbatim, including the surrounding quotes.
        /// </summary>
        internal static string Normalize(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            bool anyChange = false;

            string rewritten = QuotedScalarLineRegex.Replace(content, match =>
            {
                string body = match.Groups["body"].Value;
                if (body.IndexOf("\\u", StringComparison.Ordinal) < 0
                    && body.IndexOf("\\U", StringComparison.Ordinal) < 0)
                {
                    return match.Value;
                }

                string decodedBody = DecodeUnicodeEscapes(body, out bool changed);
                if (!changed)
                {
                    return match.Value;
                }

                anyChange = true;
                return match.Groups["lead"].Value + decodedBody + match.Groups["tail"].Value;
            });

            return anyChange ? rewritten : content;
        }

        private static string DecodeUnicodeEscapes(string body, out bool changed)
        {
            changed = false;
            // Replace each \uXXXX / \UXXXXXXXX. The other YAML backslash escapes (\\, \", \n, \t,
            // \r) are left alone — Unity emits them too, so leaving them quoted keeps the file
            // valid YAML even when the scalar still has to stay double-quoted.
            bool localChanged = false;
            string result = UnicodeEscapeRegex.Replace(body, m =>
            {
                var hex4 = m.Groups["hex"];
                if (hex4.Success)
                {
                    int codepoint = int.Parse(hex4.Value, System.Globalization.NumberStyles.HexNumber);
                    localChanged = true;
                    return char.ConvertFromUtf32(codepoint);
                }
                var hex8 = m.Groups["hex8"];
                if (hex8.Success)
                {
                    int codepoint = int.Parse(hex8.Value, System.Globalization.NumberStyles.HexNumber);
                    if (codepoint > 0x10FFFF)
                    {
                        return m.Value;
                    }
                    localChanged = true;
                    return char.ConvertFromUtf32(codepoint);
                }
                return m.Value;
            });

            changed = localChanged;
            return result;
        }
    }
}
