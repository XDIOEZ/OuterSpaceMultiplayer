using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    // CLR types are selected exclusively from the game's compiled field/method signatures.
    // No network supplied type name is ever passed to Type.GetType or MakeGenericType.
    internal static class NativeWire
    {
        internal static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        { TypeNameHandling = TypeNameHandling.None, MaxDepth = 128, MissingMemberHandling = MissingMemberHandling.Error };
        internal static string Json(object value) { return JsonConvert.SerializeObject(value, JsonSettings); }
        internal static T Parse<T>(string value) { return JsonConvert.DeserializeObject<T>(value, JsonSettings); }
        private static readonly NativeTypeBinder Binder = new NativeTypeBinder();
        private static readonly JsonSerializerSettings NativeSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            SerializationBinder = Binder,
            ContractResolver = new NativeFieldsResolver(),
            MaxDepth = 256,
            ReferenceLoopHandling = ReferenceLoopHandling.Error,
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };
        internal static byte[] Bytes(object value)
        {
            Prepare(value, true, new HashSet<object>(ReferenceIdentity.Instance));
            return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, typeof(object), NativeSettings));
        }
        internal static string Pack(object value) { return Convert.ToBase64String(Bytes(value)); }
        internal static T Unpack<T>(string value) { return (T)Unpack(value, typeof(T)); }
        internal static object Unpack(string value, Type nativeType)
        {
            Binder.Require(nativeType);
            byte[] bytes = Convert.FromBase64String(value);
            object result = JsonConvert.DeserializeObject(System.Text.Encoding.UTF8.GetString(bytes), nativeType, NativeSettings);
            if (result != null && !nativeType.IsInstanceOfType(result)) throw new JsonSerializationException("Native root type mismatch.");
            Prepare(result, false, new HashSet<object>(ReferenceIdentity.Instance));
            return result;
        }
        private sealed class ReferenceIdentity : IEqualityComparer<object>
        {
            internal static readonly ReferenceIdentity Instance = new ReferenceIdentity();
            public new bool Equals(object a, object b) { return object.ReferenceEquals(a, b); }
            public int GetHashCode(object value) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value); }
        }
        private static void Prepare(object value, bool before, HashSet<object> visited)
        {
            if (value == null || value is string || value.GetType().IsValueType || !visited.Add(value)) return;
            if (before && value is FWBaseBean) ((FWBaseBean)value).OnBeforeSerialize();
            if (value is IDictionary)
                foreach (DictionaryEntry pair in (IDictionary)value) { Prepare(pair.Key, before, visited); Prepare(pair.Value, before, visited); }
            else if (value is IEnumerable) foreach (object item in (IEnumerable)value) Prepare(item, before, visited);
            else foreach (FieldInfo field in PersistentFields(value.GetType())) Prepare(field.GetValue(value), before, visited);
            if (!before && value is FWBaseBean) ((FWBaseBean)value).OnAfterDeserialize();
        }
        private sealed class NativeFieldsResolver : DefaultContractResolver
        {
            protected override JsonObjectContract CreateObjectContract(Type type)
            {
                JsonObjectContract contract = base.CreateObjectContract(type);
                NativeArchiveCreation.Configure(type, contract);
                return contract;
            }
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                Binder.Require(type);
                return PersistentFields(type).Select(field =>
                {
                    var property = base.CreateProperty(field, MemberSerialization.Fields);
                    property.Readable = true; property.Writable = true;
                    return property;
                }).ToList();
            }
        }
        // Compute a closed graph from actual native archive roots before reading network data.
        // No call to Type.GetType, Assembly.Load, Activator, or a network-selected assembly is made.
        private sealed class NativeTypeBinder : ISerializationBinder
        {
            private readonly Dictionary<string, Type> allowed = new Dictionary<string, Type>(StringComparer.Ordinal);
            internal NativeTypeBinder()
            {
                var pending = new Queue<Type>(new[] { typeof(FWGameData), typeof(FWStarMapInfo), typeof(UserFWStarSpread), typeof(FWStartConfig), typeof(FWBaseBean) });
                foreach (Type type in typeof(FWBaseBean).Assembly.GetTypes())
                    if (!type.IsAbstract && IsClosedType(type) && typeof(FWBaseBean).IsAssignableFrom(type)) pending.Enqueue(type);
                while (pending.Count > 0)
                {
                    Type type = pending.Dequeue();
                    // Open native Bean definitions expose T / List<T> fields. They are schema
                    // templates, never constructible archive types; some have FullName == null.
                    if (!IsClosedType(type)) continue;
                    if (allowed.ContainsKey(type.FullName)) continue;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || type.IsPointer || type.IsByRef) continue;
                    if (!(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(object)
                        || type.Assembly == typeof(FWBaseBean).Assembly || type.Assembly == typeof(Vector3).Assembly
                        || type.IsArray || IsContainer(type))) continue;
                    allowed.Add(type.FullName, type);
                    if (type.IsArray) pending.Enqueue(type.GetElementType());
                    else if (type.IsGenericType) foreach (Type argument in type.GetGenericArguments()) pending.Enqueue(argument);
                    if (!type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal))
                        foreach (FieldInfo field in PersistentFields(type)) pending.Enqueue(field.FieldType);
                }
            }
            private static bool IsContainer(Type type)
            {
                if (!type.IsGenericType) return false;
                Type definition = type.GetGenericTypeDefinition();
                return definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>) || definition == typeof(Nullable<>);
            }
            private static bool IsClosedType(Type type)
            {
                return type != null && !type.IsGenericParameter && !type.ContainsGenericParameters
                    && !type.IsGenericTypeDefinition && !string.IsNullOrEmpty(type.FullName);
            }
            internal void Require(Type type)
            {
                Type expected;
                if (!IsClosedType(type) || !allowed.TryGetValue(type.FullName, out expected) || expected != type)
                    throw new JsonSerializationException("Native archive type is not a closed member of the compiled archive graph: " + (type == null ? "<null>" : type.ToString()));
            }
            public Type BindToType(string assemblyName, string typeName)
            {
                Type result;
                if (!string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(typeName) || !allowed.TryGetValue(typeName, out result))
                    throw new JsonSerializationException("Rejected native archive type token.");
                return result;
            }
            public void BindToName(Type serializedType, out string assemblyName, out string typeName)
            { Require(serializedType); assemblyName = null; typeName = serializedType.FullName; }
        }
        internal static FieldInfo Field(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
        internal static object Get(object value, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null ? property.GetValue(value, null) : Field(value.GetType(), name).GetValue(value);
        }
        internal static void Set(object value, string name, object contents)
        {
            PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite) property.SetValue(value, contents, null);
            else Field(value.GetType(), name).SetValue(value, contents);
        }
        internal static object Call(object value, string name, params object[] arguments)
        {
            Type type = value as Type ?? value.GetType();
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == name && m.GetParameters().Length >= arguments.Length && !m.IsGenericMethodDefinition)
                .Where(m => m.GetParameters().Skip(arguments.Length).All(p => p.IsOptional)).ToArray();
            MethodInfo method = methods.SingleOrDefault(m => m.GetParameters().Take(arguments.Length).Select((p, i) =>
                arguments[i] == null ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(arguments[i])).All(x => x));
            if (method == null) throw new MissingMethodException(type.FullName, name + " (matching native signature)");
            object[] actual = new object[method.GetParameters().Length];
            for (int i = 0; i < actual.Length; i++) actual[i] = i < arguments.Length ? arguments[i] : method.GetParameters()[i].DefaultValue;
            try { return method.Invoke(value is Type ? null : value, actual); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        internal static long Uid(object bean)
        {
            if (bean is FWStarMapInfo) return ((FWStarMapInfo)bean).starMapId;
            if (bean is FWUidItem) return ((FWUidItem)bean).getUID();
            throw new InvalidOperationException("Entity is not a native persistent Bean: " + bean.GetType().Name);
        }
        internal static IEnumerable<FieldInfo> PersistentFields(Type type)
        {
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => !f.IsStatic && !f.IsInitOnly && !f.IsNotSerialized).OrderBy(f => f.Name, StringComparer.Ordinal);
        }
        internal static void Copy(object source, object destination, params string[] except)
        {
            if (source == null || destination == null || source.GetType() != destination.GetType())
                throw new InvalidOperationException("Native Bean type changed without a remove/create transaction.");
            foreach (FieldInfo field in PersistentFields(source.GetType()))
            {
                if (except.Contains(field.Name)) continue;
                object next = field.GetValue(source), previous = field.GetValue(destination);
                if (next is IList && previous is IList && !field.FieldType.IsArray)
                {
                    IList target = (IList)previous; target.Clear();
                    foreach (object item in (IList)next) target.Add(item);
                }
                else field.SetValue(destination, next);
            }
        }
    }
}
