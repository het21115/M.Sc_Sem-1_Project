namespace IPOInvestmentManagement.Services
{
    public static class RupeeWordsHelper
    {
        private static readonly string[] UnitsMap =
        {
            "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
            "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
        };

        private static readonly string[] TensMap =
        {
            "Zero", "Ten", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"
        };

        public static string ToWords(decimal amount)
        {
            if (amount <= 0m)
            {
                return "Zero Rupees Only";
            }

            long wholePart = (long)Math.Truncate(amount);
            int paisePart = (int)Math.Round((amount - wholePart) * 100m, 0, MidpointRounding.AwayFromZero);

            string words = ConvertWholeNumberToWords(wholePart) + " Rupees";

            if (paisePart > 0)
            {
                words += " and " + ConvertWholeNumberToWords(paisePart) + " Paise";
            }

            return words + " Only";
        }

        private static string ConvertWholeNumberToWords(long number)
        {
            if (number == 0)
                return "Zero";

            if (number < 0)
                return "Minus " + ConvertWholeNumberToWords(Math.Abs(number));

            string words = string.Empty;

            // Crores (1,00,00,000)
            if (number / 10000000 > 0)
            {
                words += ConvertWholeNumberToWords(number / 10000000) + " Crore ";
                number %= 10000000;
            }

            // Lakhs (1,00,000)
            if (number / 100000 > 0)
            {
                words += ConvertWholeNumberToWords(number / 100000) + " Lakh ";
                number %= 100000;
            }

            // Thousands (1,000)
            if (number / 1000 > 0)
            {
                words += ConvertWholeNumberToWords(number / 1000) + " Thousand ";
                number %= 1000;
            }

            // Hundreds (100)
            if (number / 100 > 0)
            {
                words += ConvertWholeNumberToWords(number / 100) + " Hundred ";
                number %= 100;
            }

            if (number > 0)
            {
                if (words != string.Empty)
                    words += "and ";

                if (number < 20)
                {
                    words += UnitsMap[number];
                }
                else
                {
                    words += TensMap[number / 10];
                    if (number % 10 > 0)
                        words += " " + UnitsMap[number % 10];
                }
            }

            return words.Trim();
        }
    }
}
