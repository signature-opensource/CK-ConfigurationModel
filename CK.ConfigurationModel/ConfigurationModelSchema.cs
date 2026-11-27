using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace CK.Core;

/// <summary>
/// Writes the description of a configuration model (see <see cref="ConfigurationModelDescription"/>) as a JSON Schema
/// (2020-12): what the tools (the configurator's editor, a JSON editor through a <c>$schema</c>) use to complete and check a
/// section.
/// <list type="bullet">
///   <item>A model is an object schema: its properties, <c>required</c>, <c>additionalProperties: false</c> (an unknown key
///   is an error), its documentation in <c>description</c>. A nested model is a <c>$ref</c> to its <c>$defs</c>.</item>
///   <item>A value: <c>string</c>, <c>boolean</c>, <c>integer</c>, <c>number</c>; an enum lists its names; a <c>Guid</c>
///   and an <c>Uri</c> are strings of format <c>uuid</c> and <c>uri</c>; a <c>TimeSpan</c> a string with
///   <c>x-ck-format: "timespan"</c> (the .NET constant format: <c>"00:05:00"</c>, <c>"1.00:00:00"</c>; the <c>duration</c>
///   format of JSON Schema is ISO 8601, another syntax).</item>
///   <item>A list is an <c>array</c> (a list of values also accepts a comma separated <c>string</c>); a dictionary an
///   <c>object</c> whose <c>additionalProperties</c> is the model of its entries.</item>
///   <item>The default value of a basic value (its initializer) is its <c>default</c>.</item>
///   <item>An opaque property is a <c>$ref</c> to its schema (an empty schema without one).</item>
///   <item>The CK vocabulary (annotations): <c>x-ck-level</c> (<c>"Compile"</c>, <c>"Init"</c>, <c>"Run"</c>),
///   <c>x-ck-secret</c> and <c>x-ck-perDeployment</c> (<c>true</c> when they apply), <c>x-ck-format</c> (the syntax of a
///   string value that JSON Schema doesn't name: <c>"timespan"</c>).</item>
/// </list>
/// </summary>
public static class ConfigurationModelSchema
{
    /// <summary>
    /// The JSON Schema dialect: 2020-12.
    /// </summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// Writes the JSON Schema of a model.
    /// </summary>
    /// <param name="description">The model description.</param>
    /// <returns>The JSON Schema.</returns>
    public static JsonObject ToJsonSchema( ConfigurationModelDescription description )
    {
        Throw.CheckNotNullArgument( description );
        var defs = new Dictionary<Type, JsonObject>();
        var root = WriteModel( description, defs, isRoot: true );
        root.Insert( 0, "$schema", Dialect );
        if( defs.Count > 0 )
        {
            var o = new JsonObject();
            foreach( var (t, schema) in defs ) o[DefName( t )] = schema;
            root["$defs"] = o;
        }
        return root;
    }

    static string DefName( Type t ) => t.FullName ?? t.Name;

    static JsonObject WriteModel( ConfigurationModelDescription d, Dictionary<Type, JsonObject> defs, bool isRoot )
    {
        var o = new JsonObject { ["type"] = "object" };
        if( d.Documentation != null ) o["description"] = d.Documentation;
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach( var p in d.Properties )
        {
            var s = WriteProperty( p, defs );
            if( p.Documentation != null ) s["description"] = p.Documentation;
            if( p.DefaultValue != null ) s["default"] = ToJson( p.DefaultValue );
            if( p.Level != ConfigurationLevel.Init ) s["x-ck-level"] = p.Level.ToString();
            if( p.IsSecret ) s["x-ck-secret"] = true;
            if( p.IsPerDeployment ) s["x-ck-perDeployment"] = true;
            if( p.IsNoDefaults ) s["x-ck-noDefaults"] = true;
            properties[p.Name] = s;
            if( p.IsRequired ) required.Add( p.Name );
        }
        o["properties"] = properties;
        if( required.Count > 0 ) o["required"] = required;
        o["additionalProperties"] = false;
        return o;
    }

    static JsonObject Ref( ConfigurationModelDescription d, Dictionary<Type, JsonObject> defs )
    {
        if( !defs.ContainsKey( d.ModelType ) )
        {
            // Registered before its properties are written: a recursive model refers to itself.
            defs[d.ModelType] = new JsonObject();
            var schema = WriteModel( d, defs, isRoot: false );
            defs[d.ModelType] = schema;
        }
        return new JsonObject { ["$ref"] = $"#/$defs/{DefName( d.ModelType )}" };
    }

    static JsonObject WriteProperty( ConfigurationPropertyDescription p, Dictionary<Type, JsonObject> defs )
    {
        switch( p.Kind )
        {
            case ConfigurationPropertyKind.Value:
                return WriteValue( p.ValueType! );
            case ConfigurationPropertyKind.Model:
                return Ref( p.Model!, defs );
            case ConfigurationPropertyKind.List:
                if( p.Model != null ) return new JsonObject { ["type"] = "array", ["items"] = Ref( p.Model, defs ) };
                return new JsonObject
                {
                    ["anyOf"] = new JsonArray( new JsonObject { ["type"] = "array", ["items"] = WriteValue( p.ValueType! ) },
                                               new JsonObject { ["type"] = "string" } )
                };
            case ConfigurationPropertyKind.Dictionary:
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = p.Model != null ? Ref( p.Model, defs ) : WriteValue( p.ValueType! ) };
            default:
                return p.SchemaUri != null ? new JsonObject { ["$ref"] = p.SchemaUri } : new JsonObject();
        }
    }

    static JsonObject WriteValue( Type t )
    {
        // A secret value is a string in a file: its handle is the configurator's (x-ck-secret).
        if( t == typeof( string ) || t == typeof( ConfigurationSecretValue ) ) return new JsonObject { ["type"] = "string" };
        if( t == typeof( TimeSpan ) ) return new JsonObject { ["type"] = "string", ["x-ck-format"] = "timespan" };
        if( t == typeof( bool ) ) return new JsonObject { ["type"] = "boolean" };
        if( t == typeof( int ) || t == typeof( long ) ) return new JsonObject { ["type"] = "integer" };
        if( t == typeof( double ) || t == typeof( decimal ) ) return new JsonObject { ["type"] = "number" };
        if( t == typeof( Guid ) ) return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if( t == typeof( Uri ) ) return new JsonObject { ["type"] = "string", ["format"] = "uri" };
        if( t.IsEnum )
        {
            var names = new JsonArray();
            foreach( var n in Enum.GetNames( t ) ) names.Add( n );
            return new JsonObject { ["type"] = "string", ["enum"] = names };
        }
        return new JsonObject();
    }

    // A basic value as it is written in a configuration (the invariant culture).
    static JsonNode ToJson( object value ) => value switch
    {
        bool b => JsonValue.Create( b ),
        int i => JsonValue.Create( i ),
        long l => JsonValue.Create( l ),
        double d => JsonValue.Create( d ),
        decimal m => JsonValue.Create( m ),
        TimeSpan t => JsonValue.Create( t.ToString( "c", System.Globalization.CultureInfo.InvariantCulture ) ),
        _ => JsonValue.Create( value.ToString() )!
    };
}
