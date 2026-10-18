using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RlValheimLink.Contract
{
    /// <summary>
    /// The one JSON writer for every message the mod emits: compact, UTF-8 without BOM,
    /// nulls written (the contract has nullable fields), invariant culture.
    /// </summary>
    public static class MessageJson
    {
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
            FloatFormatHandling = FloatFormatHandling.Symbol, // NaN/Infinity become bare symbols → the validator rejects them
            DateParseHandling = DateParseHandling.None,
            Culture = CultureInfo.InvariantCulture,
        };

        public static string Serialize(LinkMessage message) => JsonConvert.SerializeObject(message, Settings);

        public static byte[] SerializeToBytes(LinkMessage message) => Utf8NoBom.GetBytes(Serialize(message));

        public static JObject ToToken(LinkMessage message) => (JObject)Parse(Serialize(message));

        private static readonly JsonLoadSettings ParseSettings = new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore,
        };

        /// <summary>Parse JSON text into a token tree without any date/number coercion surprises.</summary>
        public static JToken Parse(string json)
        {
            using (var sr = new StringReader(json))
            using (var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                var token = JToken.ReadFrom(jr, ParseSettings);
                if (jr.Read()) throw new JsonReaderException("trailing content after the JSON value");
                return token;
            }
        }

        public static JToken Parse(byte[] utf8) => Parse(Utf8NoBom.GetString(utf8));

        /// <summary>Compact re-serialisation of a token tree (used after server-side enrichment).</summary>
        public static byte[] ToBytes(JToken token) => Utf8NoBom.GetBytes(token.ToString(Formatting.None));

        /// <summary>capturedAt as the contract wants it: UTC, Z, second precision (≤ 24 chars).</summary>
        public static string Timestamp(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        public static bool TryParseTimestamp(string s, out DateTime utc)
        {
            return DateTime.TryParseExact(s, new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.f'Z'", "yyyy-MM-dd'T'HH:mm:ss.ff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'" },
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }
    }
}
