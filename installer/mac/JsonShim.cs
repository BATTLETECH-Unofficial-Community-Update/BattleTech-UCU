// the 2 members of System.Web.Script.Serialization that Unlock.cs and Remote.cs use, over
// System.Text.Json, so both files compile unchanged on .NET 10. Same shapes: objects are Dictionary<string, object>, arrays
// object[], integers int/long, other numbers double.
#if NET
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace System.Web.Script.Serialization
{
    public sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }

        public string Serialize(object o) { return JsonSerializer.Serialize(o); }

        public T Deserialize<T>(string text)
        {
            object o = DeserializeObject(text);
            if (o is T) return (T)o;
            if (o == null && !typeof(T).IsValueType) return default(T);
            throw new ArgumentException("The JSON isn't the expected kind of value.");   // .NET Framework's serializer also throws a type the callers catch
        }

        public object DeserializeObject(string text)
        {
            try { using (JsonDocument d = JsonDocument.Parse(text)) return Value(d.RootElement); }
            catch (JsonException e) { throw new ArgumentException(e.Message, e); }
        }

        static object Value(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: return e.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value));
                case JsonValueKind.Array: return e.EnumerateArray().Select(Value).ToArray();
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number:
                    int i; long l;
                    return e.TryGetInt32(out i) ? i : e.TryGetInt64(out l) ? (object)l : e.GetDouble();
                default: return null;
            }
        }
    }
}
#endif
