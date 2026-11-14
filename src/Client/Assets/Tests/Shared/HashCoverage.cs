#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Mmorpg.SharedTests
{
    // Compiled into both the Unity EditMode tests and the .NET tests. Checks by mutation that the
    // hashes a Stamp function writes cover every serialized field of a compiled document: it
    // builds a fully populated sample, changes one value at a time, stamps again, and compares.
    // It returns findings instead of asserting, so each test framework asserts on them.
    //
    // Paths are type level, such as Definitions[].Bag.Slots[].Kind, where [] stands for every
    // element of an array.
    public static class HashCoverage
    {
        private const int ArrayLength = 2;

        // A deterministic instance of a document type in which every string is distinct and
        // non-empty, every number distinct and non-zero, every array holds two elements, and
        // every nested object exists. A field type it cannot build throws with the field's path.
        public static T Sample<T>()
            where T : class, new()
        {
            var counter = 0;
            return (T)Build(typeof(T), string.Empty, ref counter);
        }

        // Every finding, empty when the hashes cover the document:
        // - stamping is deterministic and stamping twice gives the same revision;
        // - a leaf that Stamp overwrites, such as Revision or a StructuralFingerprint, is derived;
        // - changing any other leaf changes the revision, unless its path is in unhashed, where
        //   it must exist and must not change the revision;
        // - removing the last element of any array and swapping its two elements change it;
        // - setting an object path listed in optional to null changes it.
        public static IReadOnlyList<string> Check<T>(
            Func<T> sample,
            Func<T, string> stamp,
            string stampName,
            IDictionary<string, string> unhashed,
            IEnumerable<string> optional)
            where T : class
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            if (stamp == null)
            {
                throw new ArgumentNullException(nameof(stamp));
            }

            unhashed = unhashed ?? new Dictionary<string, string>();
            var optionalPaths = (optional ?? Enumerable.Empty<string>()).ToArray();
            var findings = new List<string>();
            var shape = Shape.Of(sample());

            var first = sample();
            var baseline = TryStamp(stamp, first, stampName, "stamping the sample", findings);
            if (baseline == null)
            {
                return findings;
            }

            var again = TryStamp(stamp, first, stampName, "stamping the sample again", findings);
            var fresh = TryStamp(stamp, sample(), stampName, "stamping a second sample", findings);
            if (!string.Equals(baseline, again, StringComparison.Ordinal)
                || !string.Equals(baseline, fresh, StringComparison.Ordinal))
            {
                findings.Add(stampName + " is not deterministic: stamping the same content twice gives "
                    + "different revisions.");
                return findings;
            }

            var derived = DerivedLeaves(sample, stamp, shape);
            var changedPaths = new HashSet<string>(StringComparer.Ordinal);
            var unchangedExamples = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var leaf in shape.Leaves)
            {
                if (derived.Contains(leaf.Concrete))
                {
                    continue;
                }

                var copy = sample();
                leaf.Set(copy, Mutate(leaf.Get(copy)));
                var revision = TryStamp(stamp, copy, stampName, "after changing " + leaf.Concrete, findings);
                if (revision == null)
                {
                    continue;
                }

                if (string.Equals(revision, baseline, StringComparison.Ordinal))
                {
                    if (!unchangedExamples.ContainsKey(leaf.Path))
                    {
                        unchangedExamples.Add(leaf.Path, leaf.Concrete);
                    }
                }
                else
                {
                    changedPaths.Add(leaf.Path);
                }
            }

            foreach (var pair in unchangedExamples.Where(pair => !unhashed.ContainsKey(pair.Key)))
            {
                findings.Add("`" + pair.Key + "` is serialized but does not change the revision (" + pair.Value
                    + " did not). Hash it in " + stampName + ", or list it as unhashed with the reason.");
            }

            var leafPaths = new HashSet<string>(shape.Leaves.Select(leaf => leaf.Path), StringComparer.Ordinal);
            var derivedPaths = new HashSet<string>(
                shape.Leaves.Where(leaf => derived.Contains(leaf.Concrete)).Select(leaf => leaf.Path),
                StringComparer.Ordinal);
            foreach (var pair in unhashed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!leafPaths.Contains(pair.Key))
                {
                    findings.Add("`" + pair.Key + "` is listed as unhashed but is not a serialized field of "
                        + typeof(T).Name + ". Remove it from the unhashed list.");
                }
                else if (derivedPaths.Contains(pair.Key))
                {
                    findings.Add("`" + pair.Key + "` is listed as unhashed but " + stampName
                        + " writes it. Remove it from the unhashed list.");
                }
                else if (changedPaths.Contains(pair.Key))
                {
                    findings.Add("`" + pair.Key + "` is listed as unhashed but changes the revision. "
                        + "Remove it from the unhashed list.");
                }
                else if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    findings.Add("`" + pair.Key + "` is listed as unhashed without a reason. Give the reason.");
                }
            }

            foreach (var array in shape.Arrays)
            {
                CheckArray(sample, stamp, stampName, baseline, array, findings);
            }

            foreach (var path in optionalPaths)
            {
                var objects = shape.Objects.Where(node => node.Path == path).ToArray();
                if (objects.Length == 0)
                {
                    findings.Add("`" + path + "` is listed as optional but is not an object field of "
                        + typeof(T).Name + ".");
                    continue;
                }

                foreach (var node in objects)
                {
                    var copy = sample();
                    node.Set(copy, null);
                    var revision = TryStamp(stamp, copy, stampName, "with " + node.Concrete + " set to null", findings);
                    if (revision != null && string.Equals(revision, baseline, StringComparison.Ordinal))
                    {
                        findings.Add("Setting `" + path + "` to null (" + node.Concrete
                            + ") does not change the revision. Hash whether it is present in " + stampName + ".");
                    }
                }
            }

            return findings;
        }

        // The sorted type-level paths whose change changes the values that select picks from the
        // stamped document, such as every definition's structural fingerprint. A path listed in
        // optional counts when setting it to null changes them. Stamp exceptions are thrown.
        public static IReadOnlyList<string> FieldsThatChange<T>(
            Func<T> sample,
            Func<T, string> stamp,
            Func<T, IEnumerable<string>> select,
            IEnumerable<string> optional)
            where T : class
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            if (stamp == null)
            {
                throw new ArgumentNullException(nameof(stamp));
            }

            if (select == null)
            {
                throw new ArgumentNullException(nameof(select));
            }

            var shape = Shape.Of(sample());
            var derived = DerivedLeaves(sample, stamp, shape);
            var baselineDocument = sample();
            stamp(baselineDocument);
            var baseline = select(baselineDocument).ToArray();
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var leaf in shape.Leaves.Where(leaf => !derived.Contains(leaf.Concrete)))
            {
                var copy = sample();
                leaf.Set(copy, Mutate(leaf.Get(copy)));
                stamp(copy);
                if (!baseline.SequenceEqual(select(copy), StringComparer.Ordinal))
                {
                    result.Add(leaf.Path);
                }
            }

            foreach (var path in optional ?? Enumerable.Empty<string>())
            {
                var objects = shape.Objects.Where(node => node.Path == path).ToArray();
                if (objects.Length == 0)
                {
                    throw new InvalidOperationException(
                        "`" + path + "` is listed as optional but is not an object field of " + typeof(T).Name + ".");
                }

                foreach (var node in objects)
                {
                    var copy = sample();
                    node.Set(copy, null);
                    stamp(copy);
                    if (!baseline.SequenceEqual(select(copy), StringComparer.Ordinal))
                    {
                        result.Add(path);
                    }
                }
            }

            return result.ToArray();
        }

        private static void CheckArray<T>(
            Func<T> sample,
            Func<T, string> stamp,
            string stampName,
            string baseline,
            Node array,
            List<string> findings)
            where T : class
        {
            var shortened = sample();
            var values = (Array)array.Get(shortened);
            var shorter = Array.CreateInstance(values.GetType().GetElementType(), values.Length - 1);
            Array.Copy(values, shorter, shorter.Length);
            array.Set(shortened, shorter);
            var revision = TryStamp(stamp, shortened, stampName, "after removing the last element of " + array.Concrete, findings);
            if (revision != null && string.Equals(revision, baseline, StringComparison.Ordinal))
            {
                findings.Add("Removing the last element of `" + array.Path + "` (" + array.Concrete
                    + ") does not change the revision. Hash its element count or every element in " + stampName + ".");
            }

            var swapped = sample();
            var elements = (Array)array.Get(swapped);
            var firstElement = elements.GetValue(0);
            elements.SetValue(elements.GetValue(1), 0);
            elements.SetValue(firstElement, 1);
            revision = TryStamp(stamp, swapped, stampName, "after swapping the elements of " + array.Concrete, findings);
            if (revision != null && string.Equals(revision, baseline, StringComparison.Ordinal))
            {
                findings.Add("Swapping the two elements of `" + array.Path + "` (" + array.Concrete
                    + ") does not change the revision. Hash its elements in order in " + stampName + ".");
            }
        }

        // The concrete paths of the leaves that Stamp overwrites, which it derives from the rest.
        private static HashSet<string> DerivedLeaves<T>(Func<T> sample, Func<T, string> stamp, Shape shape)
            where T : class
        {
            var built = sample();
            var stamped = sample();
            stamp(stamped);
            return new HashSet<string>(
                shape.Leaves
                    .Where(leaf => !Equals(leaf.Get(built), leaf.Get(stamped)))
                    .Select(leaf => leaf.Concrete),
                StringComparer.Ordinal);
        }

        private static string TryStamp<T>(
            Func<T, string> stamp,
            T document,
            string stampName,
            string situation,
            List<string> findings)
        {
            try
            {
                return stamp(document) ?? string.Empty;
            }
            catch (Exception exception)
            {
                findings.Add(stampName + " throws " + situation + ": " + exception.GetType().Name + ": "
                    + exception.Message);
                return null;
            }
        }

        private static object Mutate(object value)
        {
            switch (value)
            {
                case string text:
                    return text + "x";
                case int number:
                    return number + 1;
                case long number:
                    return number + 1L;
                case float number:
                    return number + 1f;
                case double number:
                    return number + 1d;
                case bool flag:
                    return !flag;
                default:
                    throw new InvalidOperationException("HashCoverage cannot change a value of type "
                        + (value == null ? "null" : value.GetType().FullName) + ".");
            }
        }

        private static bool IsLeaf(Type type)
        {
            return type == typeof(string)
                || type == typeof(int)
                || type == typeof(long)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(bool);
        }

        private static object Build(Type type, string path, ref int counter)
        {
            if (IsLeaf(type))
            {
                counter++;
                if (type == typeof(string))
                {
                    return "v" + counter.ToString(CultureInfo.InvariantCulture);
                }

                if (type == typeof(int))
                {
                    return counter;
                }

                if (type == typeof(long))
                {
                    return (long)counter;
                }

                if (type == typeof(float))
                {
                    return (float)counter;
                }

                if (type == typeof(double))
                {
                    return (double)counter;
                }

                return counter % 2 == 0;
            }

            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var array = Array.CreateInstance(elementType, ArrayLength);
                for (var index = 0; index < ArrayLength; index++)
                {
                    array.SetValue(Build(elementType, path + "[]", ref counter), index);
                }

                return array;
            }

            if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new NotSupportedException("HashCoverage cannot build `" + Display(path) + "` of type "
                    + type.FullName + ". Teach HashCoverage this kind of field.");
            }

            var instance = Activator.CreateInstance(type);
            foreach (var field in SerializedFields(type, path))
            {
                field.SetValue(instance, Build(field.FieldType, Join(path, field.Name), ref counter));
            }

            return instance;
        }

        // Compiled documents serialize their public instance fields and nothing else. Anything
        // that a serializer might also write, or might skip, fails loudly.
        private static IEnumerable<FieldInfo> SerializedFields(Type type, string path)
        {
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (properties.Length > 0)
            {
                throw new NotSupportedException("`" + Display(path) + "` of type " + type.FullName + " has property "
                    + properties[0].Name + ". HashCoverage supports documents of public fields only.");
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (!field.IsPublic || field.IsInitOnly)
                {
                    throw new NotSupportedException("`" + Join(path, field.Name) + "` is not a public writable field. "
                        + "HashCoverage supports documents of public fields only.");
                }

                if (field.IsDefined(typeof(NonSerializedAttribute), false))
                {
                    throw new NotSupportedException("`" + Join(path, field.Name) + "` is [NonSerialized]. "
                        + "HashCoverage supports documents of serialized fields only.");
                }
            }

            return fields.OrderBy(field => field.MetadataToken);
        }

        private static string Join(string path, string name)
        {
            return path.Length == 0 ? name : path + "." + name;
        }

        private static string Display(string path)
        {
            return path.Length == 0 ? "the document" : path;
        }

        // A field or array element of the sample, found again in any other sample by its steps.
        private sealed class Node
        {
            private readonly object[] steps;

            public Node(string path, string concrete, object[] steps)
            {
                Path = path;
                Concrete = concrete;
                this.steps = steps;
            }

            public string Path { get; }

            public string Concrete { get; }

            public object Get(object root)
            {
                return Read(Parent(root), steps[steps.Length - 1]);
            }

            public void Set(object root, object value)
            {
                var parent = Parent(root);
                var step = steps[steps.Length - 1];
                if (step is FieldInfo field)
                {
                    field.SetValue(parent, value);
                }
                else
                {
                    ((Array)parent).SetValue(value, (int)step);
                }
            }

            private object Parent(object root)
            {
                var current = root;
                for (var index = 0; index < steps.Length - 1; index++)
                {
                    current = Read(current, steps[index]);
                }

                return current;
            }

            private static object Read(object parent, object step)
            {
                return step is FieldInfo field
                    ? field.GetValue(parent)
                    : ((Array)parent).GetValue((int)step);
            }
        }

        private sealed class Shape
        {
            public readonly List<Node> Leaves = new List<Node>();
            public readonly List<Node> Arrays = new List<Node>();
            public readonly List<Node> Objects = new List<Node>();

            public static Shape Of(object document)
            {
                var shape = new Shape();
                shape.Visit(document, document.GetType(), string.Empty, string.Empty, new List<object>());
                return shape;
            }

            private void Visit(object value, Type type, string path, string concrete, List<object> steps)
            {
                if (IsLeaf(type))
                {
                    Leaves.Add(new Node(path, concrete, steps.ToArray()));
                    return;
                }

                if (value == null)
                {
                    return;
                }

                if (type.IsArray)
                {
                    var array = (Array)value;
                    Arrays.Add(new Node(path, concrete, steps.ToArray()));
                    for (var index = 0; index < array.Length; index++)
                    {
                        steps.Add(index);
                        Visit(
                            array.GetValue(index),
                            type.GetElementType(),
                            path + "[]",
                            concrete + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                            steps);
                        steps.RemoveAt(steps.Count - 1);
                    }

                    return;
                }

                if (steps.Count > 0)
                {
                    Objects.Add(new Node(path, concrete, steps.ToArray()));
                }

                foreach (var field in SerializedFields(type, path))
                {
                    steps.Add(field);
                    Visit(field.GetValue(value), field.FieldType, Join(path, field.Name), Join(concrete, field.Name), steps);
                    steps.RemoveAt(steps.Count - 1);
                }
            }
        }
    }
}
