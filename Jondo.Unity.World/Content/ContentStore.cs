using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// Rows keyed by something, loaded from several layers, merged by precedence, each one
    /// remembering where it came from.
    /// </summary>
    /// <typeparam name="TKey">What identifies a row. Must be stable across layers.</typeparam>
    /// <typeparam name="TValue">The row itself.</typeparam>
    /// <remarks>
    /// Load order does not matter: a row is only replaced by one from a layer that is at least as
    /// high, so a base file read after an authored file cannot undo it. That is deliberate — the
    /// loaders run in whatever order startup happens to call them, and a rule that depends on the
    /// order is a rule that breaks the first time somebody reshuffles initialisation.
    ///
    /// The authored layer can also <em>delete</em> a row it did not write, which is what
    /// <see cref="Erase"/> is for: a monster group or an NPC that Ankama places and we do not want
    /// has to be removable without editing the generated file it came from.
    /// </remarks>
    public sealed class ContentStore<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, Sourced<TValue>> _rows = new Dictionary<TKey, Sourced<TValue>>();

        /// <summary>Keys the authored layer has removed. They stay out whoever puts them back.</summary>
        private readonly Dictionary<TKey, Origin> _erased = new Dictionary<TKey, Origin>();

        public int Count => _rows.Count;

        /// <summary>How many rows survived from each layer. For the log and for the editor.</summary>
        public IReadOnlyDictionary<ContentLayer, int> Census()
        {
            var census = new Dictionary<ContentLayer, int>
            {
                [ContentLayer.Base] = 0,
                [ContentLayer.Measured] = 0,
                [ContentLayer.Authored] = 0,
            };
            foreach (var row in _rows.Values) census[row.From.Layer]++;
            return census;
        }

        /// <summary>How many rows the authored layer has taken out.</summary>
        public int ErasedCount => _erased.Count;

        /// <summary>
        /// Which rows the authored layer has taken out.
        /// </summary>
        /// <remarks>
        /// The editor needs these to write the file back: a tombstone that is loaded and not
        /// written out again would let the next regeneration put the row it removed back, which is
        /// exactly the silent undo the layers exist to prevent.
        /// </remarks>
        public IEnumerable<TKey> ErasedKeys => _erased.Keys;

        /// <summary>
        /// Adds a row, unless something from a higher layer already holds that key, or the authored
        /// layer has erased it.
        /// </summary>
        /// <returns>True when the row went in.</returns>
        public bool Put(TKey key, TValue value, Origin from)
        {
            if (_erased.ContainsKey(key)) return false;

            if (_rows.TryGetValue(key, out var already) && already.From.Layer > from.Layer)
            {
                return false;
            }

            _rows[key] = new Sourced<TValue>(value, from);
            return true;
        }

        /// <summary>
        /// Takes a row out and keeps it out. Only the authored layer gets to do this: erasing from
        /// a regenerable layer would be undone by the next regeneration anyway.
        /// </summary>
        public void Erase(TKey key, Origin from)
        {
            if (from.Layer != ContentLayer.Authored)
            {
                throw new InvalidOperationException(
                    $"Only the authored layer can erase rows; this came from {from.Layer}. " +
                    "A generated layer that wants a row gone should stop generating it.");
            }

            _erased[key] = from;
            _rows.Remove(key);
        }

        public bool TryGet(TKey key, out Sourced<TValue> row) => _rows.TryGetValue(key, out row);

        public bool Contains(TKey key) => _rows.ContainsKey(key);

        /// <summary>Where a row came from, for the editor's provenance column.</summary>
        public Origin? OriginOf(TKey key)
            => _rows.TryGetValue(key, out var row) ? row.From : (Origin?)null;

        public IEnumerable<KeyValuePair<TKey, Sourced<TValue>>> Rows => _rows;

        public IEnumerable<TValue> Values
        {
            get
            {
                foreach (var row in _rows.Values) yield return row.Value;
            }
        }

        public void Clear()
        {
            _rows.Clear();
            _erased.Clear();
        }
    }
}
