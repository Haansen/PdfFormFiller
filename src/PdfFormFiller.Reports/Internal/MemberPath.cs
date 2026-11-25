using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace PdfFormFiller.Reports.Internal;

/// <summary>A chain of properties (or fields, in lambdas) from an item, read with null anywhere on the way giving null.</summary>
internal sealed class MemberPath
{
    private Func<object, object?>? _getter;

    private MemberPath(Type root, IReadOnlyList<MemberInfo> members)
    {
        Root = root;
        Members = members;
        // Nullable<T>.Value is transparent
        Name = string.Join('.', members.Where(m => !IsNullableValue(m)).Select(m => m.Name));
        Type = TypeOf(members[^1]);
        var last = members.Last(m => !IsNullableValue(m));
        var display = last.GetCustomAttribute<DisplayAttribute>();
        Header = Read(last, () => display?.GetShortName())
                 ?? Read(last, () => last.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName)
                 ?? Humanise(last.Name);
        var format = last.GetCustomAttribute<DisplayFormatAttribute>();
        Format = string.IsNullOrEmpty(format?.DataFormatString) ? null : format.DataFormatString;
        NullText = Read(last, () => format?.GetNullDisplayText());
    }

    private Type Root { get; }
    private IReadOnlyList<MemberInfo> Members { get; }
    public string Name { get; }
    public Type Type { get; }
    public string Header { get; }
    public string? Format { get; }
    public string? NullText { get; }

    public Func<object, object?> Getter => _getter ??= Build();

    private static string? Read(MemberInfo member, Func<string?> attribute)
    {
        try { return attribute() is { Length: > 0 } s ? s : null; }
        catch (InvalidOperationException e) { throw new PdfFormException($"The attributes of '{member.Name}' cannot be read: {e.Message}", e); }
    }

    private static Type TypeOf(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;

    private static bool IsNullableValue(MemberInfo m) => m.Name == "Value" && m.DeclaringType is { } t && Nullable.GetUnderlyingType(t) is not null;

    /// <summary>The path of a lambda that only follows members, otherwise null.</summary>
    public static MemberPath? TryFrom(LambdaExpression lambda)
    {
        var members = new List<MemberInfo>();
        var e = Strip(lambda.Body);
        while (e is MemberExpression { Expression: not null } m)
        {
            if (m.Member is PropertyInfo { GetMethod.IsStatic: true }) return null;
            members.Insert(0, m.Member);
            e = Strip(m.Expression);
        }
        return members.Count > 0 && e == lambda.Parameters[0] && !members.All(IsNullableValue) ? new MemberPath(lambda.Parameters[0].Type, members) : null;

        // boxing and upcasts; a conversion that changes the value makes the expression a computed one
        static Expression Strip(Expression x)
            => x is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } u && u.Type.IsAssignableFrom(u.Operand.Type) ? Strip(u.Operand) : x;
    }

    /// <summary>Finds a property path such as "Customer.Name".</summary>
    public static MemberPath Resolve(Type root, string path)
    {
        var members = new List<MemberInfo>();
        var type = root;
        foreach (var part in path.Split('.'))
        {
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                members.Add(type.GetProperty("Value")!);
                type = underlying;
            }
            var all = Properties(type);
            var name = part.Trim();
            var hits = all.Where(p => p.Name == name).ToList();
            if (hits.Count == 0) hits = all.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count != 1)
            {
                var names = all.Count == 0 ? "It has no readable properties." : "Available: " + string.Join(", ", all.Select(p => p.Name)) + ".";
                var where = path.Contains('.') ? $" (in '{path}')" : "";
                throw new PdfFormException(hits.Count == 0 ? $"{type.Name} has no readable property '{name}'{where}. {names}" : $"'{name}'{where} fits several properties of {type.Name}. {names}");
            }
            members.Add(hits[0]);
            type = hits[0].PropertyType;
        }
        return new MemberPath(root, members);
    }

    /// <summary>The properties shown when no columns are chosen.</summary>
    public static IEnumerable<MemberPath> Automatic(Type type, HashSet<string> excluded)
    {
        var list = new List<(int Order, MemberPath Path)>();
        foreach (var p in Properties(type))
        {
            if (!IsSimple(p.PropertyType) || excluded.Contains(p.Name)) continue;
            if (p.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
            var display = p.GetCustomAttribute<DisplayAttribute>();
            if (display?.GetAutoGenerateField() == false) continue;
            list.Add((display?.GetOrder() ?? 10000, new MemberPath(type, new[] { p }))); // 10000: what DisplayAttribute documents for no order
        }
        return list.OrderBy(x => x.Order).Select(x => x.Path);
    }

    /// <summary>Public readable properties in declaration order, those of base types first.</summary>
    private static List<PropertyInfo> Properties(Type type)
    {
        var levels = new List<Type>();
        if (type.IsInterface) { levels.AddRange(type.GetInterfaces()); levels.Add(type); }
        else for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType) levels.Insert(0, t);
        var list = new List<PropertyInfo>();
        foreach (var level in levels)
            foreach (var p in level.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(p => p.MetadataToken))
            {
                if (p.GetMethod is not { IsPublic: true } || p.GetIndexParameters().Length > 0 || p.PropertyType.IsByRef || p.PropertyType.IsByRefLike || p.PropertyType.IsPointer) continue;
                var i = list.FindIndex(x => x.Name == p.Name);
                if (i >= 0) list[i] = p; // overridden or hidden: keeps its place
                else list.Add(p);
            }
        return list;
    }

    public static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)
               || t == typeof(TimeOnly) || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(Uri) || t == typeof(Half) || t == typeof(Int128) || t == typeof(UInt128)
               || t == typeof(System.Numerics.BigInteger) || t == typeof(Version);
    }

    private Func<object, object?> Build()
    {
        var item = Expression.Parameter(typeof(object));
        var end = Expression.Label(typeof(object));
        var variables = new List<ParameterExpression>();
        var body = new List<Expression>();
        Expression current = Expression.Convert(item, Root);
        foreach (var member in Members)
        {
            var v = Expression.Variable(current.Type);
            variables.Add(v);
            body.Add(Expression.Assign(v, current));
            if (!v.Type.IsValueType || Nullable.GetUnderlyingType(v.Type) is not null)
                body.Add(Expression.IfThen(Expression.Equal(v, Expression.Constant(null, v.Type)), Expression.Return(end, Expression.Constant(null))));
            // an upcast in the lambda may have been to a type the member belongs to
            Expression owner = member.DeclaringType!.IsAssignableFrom(v.Type) ? v : Expression.Convert(v, member.DeclaringType);
            current = member is PropertyInfo p ? Expression.Property(owner, p) : Expression.Field(owner, (FieldInfo)member);
        }
        body.Add(Expression.Label(end, Expression.Convert(current, typeof(object))));
        return Expression.Lambda<Func<object, object?>>(Expression.Block(variables, body), item).Compile();
    }

    /// <summary>"OrderDate" becomes "Order date", "VATNumber" "VAT number", "order_id" "Order id".</summary>
    public static string Humanise(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_') { Flush(); continue; }
            var upper = char.IsUpper(c);
            if (word.Length > 0 && upper && (!char.IsUpper(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]))) Flush();
            else if (word.Length > 0 && char.IsDigit(c) != char.IsDigit(name[i - 1])) Flush();
            word.Append(c);
        }
        Flush();
        if (words.Count == 0) return name;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var acronym = w.Length > 1 && w.All(c => char.IsUpper(c) || char.IsDigit(c));
            if (!acronym) w = w.ToLowerInvariant();
            if (i == 0) w = char.ToUpperInvariant(w[0]) + w[1..];
            words[i] = w;
        }
        return string.Join(' ', words);

        void Flush() { if (word.Length > 0) words.Add(word.ToString()); word.Clear(); }
    }
}
