using System.Text;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Contains extension methods for string manipulations.
    /// </summary>
    public static class StringExtensions
    {
        public static string RemovePrefix(this string str)
        {
            int prefixIndex = str.IndexOf('_');
            return prefixIndex != -1 ? str.Substring(prefixIndex + 1) : str;
        }
        
        public static ReadOnlySpan<char> RemovePrefix(this ReadOnlySpan<char> span)
        {
            int prefixIndex = span.IndexOf('_');
            return prefixIndex != -1 ? span.Slice(prefixIndex + 1) : span;
        }
        
        /// <summary>
        /// Adds a space before each capital letter in the input string.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>A new string with spaces added before capital letters.</returns>
        public static string AddSpaceBeforeCapitalLetters(this string input)
        {
            // Check if the input string is null or empty
            if (string.IsNullOrEmpty(input))
            {
                // Return the input string if it's null or empty
                return input;
            }

            // Create a StringBuilder to store the modified string
            var result = new StringBuilder(input.Length * 2);
        
            // Append the first character of the input string
            result.Append(input[0]);

            // Iterate through the characters of the input string starting from the second character
            for (int i = 1; i < input.Length; i++)
            {
                // Check if the current character is uppercase
                if (char.IsUpper(input[i]))
                {
                    // Add a space before the uppercase letter
                    result.Append(' ');
                }

                // Append the current character to the result
                result.Append(input[i]);
            }

            // Convert the StringBuilder to a string and return it
            return result.ToString();
        }
        
        public static int DamerauLevenshteinDistanceTo(this string string1, string string2)
        {
            if (string.IsNullOrEmpty(string1))
                return !string.IsNullOrEmpty(string2) ? string2.Length : 0;

            if (string.IsNullOrEmpty(string2))
                return !string.IsNullOrEmpty(string1) ? string1.Length : 0;

            int length1 = string1.Length;
            int length2 = string2.Length;

            int[,] d = new int[length1 + 1, length2 + 1];

            for (int i = 0; i <= d.GetUpperBound(0); i++)
                d[i, 0] = i;

            for (int i = 0; i <= d.GetUpperBound(1); i++)
                d[0, i] = i;

            for (int i = 1; i <= d.GetUpperBound(0); i++)
            {
                for (int j = 1; j <= d.GetUpperBound(1); j++)
                {
                    var cost = string1[i - 1] == string2[j - 1] ? 0 : 1;

                    var del = d[i - 1, j] + 1;
                    var ins = d[i, j - 1] + 1;
                    var sub = d[i - 1, j - 1] + cost;

                    d[i, j] = Math.Min(del, Math.Min(ins, sub));

                    if (i > 1 && j > 1 && string1[i - 1] == string2[j - 2] && string1[i - 2] == string2[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
                }
            }

            return d[d.GetUpperBound(0), d.GetUpperBound(1)];
        }
    }
}