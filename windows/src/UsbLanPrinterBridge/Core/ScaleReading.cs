using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>Exact weight-unit conversions (kg, g, lb, oz), so EU and US devices interoperate without drift.</summary>
    public static class ScaleUnits
    {
        // Grams per unit, by definition: 1 lb = 453.59237 g exactly; 1 oz = 1 lb / 16 = 28.349523125 g.
        public static bool TryGramsPer(string unit, out decimal grams)
        {
            switch ((unit ?? "").Trim().ToLowerInvariant())
            {
                case "g": grams = 1m; return true;
                case "kg": grams = 1000m; return true;
                case "lb": grams = 453.59237m; return true;
                case "oz": grams = 28.349523125m; return true;
                default: grams = 0m; return false;
            }
        }

        public static bool Known(string unit) { decimal g; return TryGramsPer(unit, out g); }

        public static bool TryToGrams(decimal weight, string unit, out decimal grams)
        {
            decimal per;
            if (!TryGramsPer(unit, out per)) { grams = 0m; return false; }
            grams = weight * per; return true;
        }

        public static bool TryConvert(decimal weight, string from, string to, out decimal result)
        {
            decimal gf, gt;
            if (!TryGramsPer(from, out gf) || !TryGramsPer(to, out gt)) { result = weight; return false; }
            result = weight * gf / gt; return true;
        }
    }

    /// <summary>One weight reading parsed from a scale's output line. Immutable; safe to hand between threads.</summary>
    public sealed class ScaleReading
    {
        public bool Ok { get; private set; }          // a weight was parsed
        public decimal Weight { get; private set; }
        public string Unit { get; private set; }      // "kg","g","lb","oz" or ""
        public bool Stable { get; private set; }       // the scale reported a settled weight
        public string Raw { get; private set; }        // the line as received (for identifying an unknown scale)
        public DateTime AtUtc { get; private set; }
        public bool HasGrams { get; private set; }     // the unit was known, so a canonical weight is available
        public decimal Grams { get; private set; }     // the weight in grams (the base for any conversion)

        private ScaleReading() { Unit = ""; Raw = ""; AtUtc = DateTime.UtcNow; }

        public static ScaleReading Weighed(decimal weight, string unit, bool stable, string raw)
        {
            var r = new ScaleReading { Ok = true, Weight = weight, Unit = unit ?? "", Stable = stable, Raw = raw ?? "", AtUtc = DateTime.UtcNow };
            decimal grams;
            if (ScaleUnits.TryToGrams(weight, r.Unit, out grams)) { r.HasGrams = true; r.Grams = grams; }
            return r;
        }

        public static ScaleReading Unparsed(string raw)
        {
            return new ScaleReading { Ok = false, Raw = raw ?? "", AtUtc = DateTime.UtcNow };
        }

        public static readonly ScaleReading Empty = new ScaleReading();

        /// <summary>The same reading expressed in another unit (kg/g/lb/oz). Returns itself when it cannot convert.</summary>
        public ScaleReading InUnit(string target)
        {
            if (!Ok || string.IsNullOrEmpty(target)) return this;
            if (string.Equals(target, Unit, StringComparison.OrdinalIgnoreCase)) return this;
            decimal converted;
            if (!ScaleUnits.TryConvert(Weight, Unit, target, out converted)) return this;
            var r = new ScaleReading
            {
                Ok = true, Weight = decimal.Round(converted, 4), Unit = target.Trim().ToLowerInvariant(),
                Stable = Stable, Raw = Raw, AtUtc = AtUtc, HasGrams = HasGrams, Grams = Grams
            };
            return r;
        }

        /// <summary>JSON the POS reads: native weight+unit, the canonical grams, stability, the raw line and age.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"ok\":").Append(Ok ? "true" : "false");
            sb.Append(",\"weight\":").Append(Weight.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"unit\":\"").Append(JsonEscape(Unit)).Append('"');
            sb.Append(",\"grams\":").Append(HasGrams ? Grams.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"stable\":").Append(Stable ? "true" : "false");
            sb.Append(",\"raw\":\"").Append(JsonEscape(Raw)).Append('"');
            sb.Append(",\"at\":\"").Append(AtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('"');
            long ageMs = (long)(DateTime.UtcNow - AtUtc).TotalMilliseconds;
            sb.Append(",\"ageMs\":").Append(ageMs < 0 ? 0 : ageMs);
            sb.Append('}');
            return sb.ToString();
        }

        internal static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Turns a raw scale line into a <see cref="ScaleReading"/>. Deliberately format-agnostic: most POS scales stream
    /// a short ASCII line with a signed decimal and a unit, optionally prefixed by a stability token (ST=stable,
    /// US=unstable). Handles the common continuous formats (generic/NCI, Toledo, CAS) without needing the model.
    /// </summary>
    public static class ScaleParser
    {
        // A weight immediately followed by a unit is the strongest signal, so it is tried first.
        private static readonly Regex NumberThenUnit = new Regex(
            @"([-+]?\d{1,7}(?:[.,]\d{1,4})?)\s*(kgs?|lbs?|ozs?|grams?|kg|lb|oz|g)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnyNumber = new Regex(
            @"[-+]?\d{1,7}(?:[.,]\d{1,4})?",
            RegexOptions.Compiled);

        private static readonly Regex StableToken = new Regex(@"\bST\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UnstableToken = new Regex(@"\b(US|MO|MOTION)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Parse one line. Returns an Ok reading when a weight is found, otherwise an Unparsed reading.</summary>
        public static ScaleReading Parse(string line)
        {
            if (line == null) return ScaleReading.Unparsed(null);
            string raw = line;

            // Keep only what a weight line is made of, so stray control bytes/framing do not defeat the regexes.
            var clean = new StringBuilder(raw.Length);
            foreach (char c in raw)
                if (c >= 0x20 && c < 0x7F) clean.Append(c);
            string s = clean.ToString().Trim();
            if (s.Length == 0) return ScaleReading.Unparsed(raw);

            bool stable = true;
            if (UnstableToken.IsMatch(s)) stable = false;
            else if (StableToken.IsMatch(s)) stable = true;

            string numText = null;
            string unit = "";

            Match mu = NumberThenUnit.Match(s);
            if (mu.Success)
            {
                numText = mu.Groups[1].Value;
                unit = NormalizeUnit(mu.Groups[2].Value);
            }
            else
            {
                Match mn = AnyNumber.Match(s);
                if (mn.Success) numText = mn.Value;
                unit = FindUnit(s);
            }

            if (numText == null) return ScaleReading.Unparsed(raw);

            decimal weight;
            if (!decimal.TryParse(numText.Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out weight))
                return ScaleReading.Unparsed(raw);

            return ScaleReading.Weighed(weight, unit, stable, raw);
        }

        private static readonly Regex UnitAlone = new Regex(@"\b(kgs?|lbs?|ozs?|grams?|kg|lb|oz|g)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string FindUnit(string s)
        {
            Match m = UnitAlone.Match(s);
            return m.Success ? NormalizeUnit(m.Value) : "";
        }

        private static string NormalizeUnit(string u)
        {
            if (string.IsNullOrEmpty(u)) return "";
            switch (u.Trim().ToLowerInvariant())
            {
                case "kg": case "kgs": return "kg";
                case "g": case "gram": case "grams": return "g";
                case "lb": case "lbs": return "lb";
                case "oz": case "ozs": return "oz";
                default: return u.ToLowerInvariant();
            }
        }
    }
}
