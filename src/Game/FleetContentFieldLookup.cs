using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeWorldMultiplayer
{
    // Only lives while decoding one authoritative fleet content delta.
    // A dense snapshot previously called FieldInfo[].SingleOrDefault on
    // the same reflection fields for every incoming field name.
    internal sealed class FleetContentFieldLookup<TField> where TField : class
    {
        private const int DenseFieldsThreshold = 8;
        private readonly IEnumerable<TField> fields;
        private readonly Func<TField, string> nameOf;
        private Dictionary<string, TField> index;
        private bool indexed;

        internal static bool ShouldIndex(int incomingCount, int knownCount)
        { return incomingCount >= DenseFieldsThreshold && knownCount >= DenseFieldsThreshold; }

        internal FleetContentFieldLookup(IEnumerable<TField> fields, Func<TField, string> nameOf)
        {
            this.fields = fields;
            this.nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
        }

        private Dictionary<string, TField> BuildIndex()
        {
            if (fields == null) return null;
            // Reflection supplies an array with a known length; reserve its
            // capacity immediately instead of repeatedly growing buckets
            // while decoding a large authoritative fleet baseline.
            var collection = fields as ICollection<TField>;
            var result = collection == null
                ? new Dictionary<string, TField>(StringComparer.Ordinal)
                : new Dictionary<string, TField>(collection.Count, StringComparer.Ordinal);
            foreach (TField field in fields)
            {
                // Original LINQ SingleOrDefault raises for null entries or
                // duplicated *requested* names. Never preemptively reject
                // an unrelated bad entry: use its exact legacy path instead.
                if (field == null) return null;
                string name = nameOf(field);
                if (name == null || result.ContainsKey(name)) return null;
                result.Add(name, field);
            }
            return result;
        }

        internal TField Find(string fieldName)
        {
            // A missing network field name is handled by the original
            // SingleOrDefault and existing caller rejection, not by the
            // dictionary's null-key exception.
            if (fieldName == null)
                return fields.SingleOrDefault(field => nameOf(field) == fieldName);
            if (!indexed)
            {
                index = BuildIndex();
                indexed = true;
            }
            if (index == null)
                return fields.SingleOrDefault(field => nameOf(field) == fieldName);
            TField fieldValue;
            return index.TryGetValue(fieldName, out fieldValue) ? fieldValue : null;
        }
    }
}
