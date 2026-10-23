using Builder.Core;
using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Builder.Mcp;

/// <summary>Schemas follow the serializer's public input properties and constructor requirements.
/// Optional Core structs/infinite ray limits deliberately have no JSON default literal.</summary>
internal static class ToolSchema
{
    private static readonly NullabilityInfoContext Nullability=new();
    public static JsonObject For(Type type)
    {
        if(Nullable.GetUnderlyingType(type) is {} underlying)return new(){["anyOf"]=new JsonArray(For(underlying),new JsonObject{["type"]="null"})};
        if(type==typeof(string))return new(){["type"]="string"};
        if(type==typeof(Guid))return new(){["type"]="string",["format"]="uuid"};
        if(type==typeof(DateTimeOffset))return new(){["type"]="string",["format"]="date-time"};
        if(type==typeof(bool))return new(){["type"]="boolean"};
        if(type.IsEnum)return new(){["type"]="string",["enum"]=new JsonArray(Enum.GetNames(type).Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray())};
        if(type.IsPrimitive)return new(){["type"]=type==typeof(double)||type==typeof(float)?"number":"integer"};
        if(type==typeof(TextSource))return Union((typeof(FontTextSource),"font"),(typeof(OutlineTextSource),"outline"));
        if(type==typeof(ReliefSource))return Union((typeof(ImageFileSource),"file"),(typeof(PixelImageSource),"pixels"));
        if(type!=typeof(string)&&typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element=type.IsArray?type.GetElementType()!:type.GetInterfaces().Append(type).Single(t=>t.IsGenericType&&t.GetGenericTypeDefinition()==typeof(IEnumerable<>)).GetGenericArguments()[0];
            return new(){["type"]="array",["items"]=For(element)};
        }
        var info=McpJson.Options.GetTypeInfo(type);var properties=new JsonObject();var required=new JsonArray();
        var parameters=type.GetConstructors().OrderByDescending(c=>c.GetParameters().Length).FirstOrDefault()?.GetParameters()??[];
        foreach(var property in info.Properties.Where(p=>p.Set is not null||parameters.Any(a=>string.Equals(a.Name,p.Name,StringComparison.OrdinalIgnoreCase))))
        {
            var schema=For(property.PropertyType);
            if(property.AttributeProvider is PropertyInfo clr&&!property.PropertyType.IsValueType&&Nullability.Create(clr).ReadState==NullabilityState.Nullable)
                schema=new(){["anyOf"]=new JsonArray(schema,new JsonObject{["type"]="null"})};
            properties[property.Name]=schema;
            if(property.IsRequired||parameters.Any(p=>!p.HasDefaultValue&&string.Equals(p.Name,property.Name,StringComparison.OrdinalIgnoreCase)))required.Add(property.Name);
        }
        return new(){["type"]="object",["properties"]=properties,["required"]=required,["additionalProperties"]=false};
    }
    private static JsonObject Union(params (Type Type,string Kind)[] cases)
    {
        var schemas=new JsonArray();
        foreach(var (type,kind) in cases)
        {
            var schema=For(type);((JsonObject)schema["properties"]!)["sourceKind"]=new JsonObject{["const"]=kind,["type"]="string"};
            ((JsonArray)schema["required"]!).Add("sourceKind");schemas.Add(schema);
        }
        return new(){["oneOf"]=schemas};
    }
}
