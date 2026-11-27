using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CK.Core;

/// <summary>
/// Builds the descriptions of the models by reflection (the tools, the App builder, the engines and the tests). The
/// rules are the ones of <see cref="ConfigurationModelAttribute"/>; the engines generate the same descriptions as code
/// (CK.ConfigurationModel.Engine reads them from here).
/// </summary>
static class ConfigurationModelReflection
{
    static readonly object _lock = new object();
    static readonly Dictionary<Type, (ConfigurationModelDescription? D, string? Error)> _cache = new();
    static readonly Dictionary<Assembly, XDocument?> _docs = new();

    public static bool TryGet( Type modelType,
                               [NotNullWhen( true )] out ConfigurationModelDescription? description,
                               [NotNullWhen( false )] out string? error )
    {
        lock( _lock )
        {
            // A model being built (a model that contains itself, directly or not) is resolved lazily by its
            // properties: its description is in the cache when they are read.
            (description, error) = Build( modelType, new HashSet<Type>() );
            return description != null;
        }
    }

    static (ConfigurationModelDescription? D, string? Error) Build( Type t, HashSet<Type> building )
    {
        if( _cache.TryGetValue( t, out var cached ) ) return cached;
        var result = DoBuild( t, building );
        _cache[t] = result;
        return result;
    }

    static (ConfigurationModelDescription? D, string? Error) DoBuild( Type t, HashSet<Type> building )
    {
        if( !t.IsClass || t.IsAbstract || t.IsGenericTypeDefinition || !t.IsDefined( typeof( ConfigurationModelAttribute ), inherit: false ) )
        {
            return (null, $"The type '{t.FullName}' is not a configuration model: it must be a concrete [ConfigurationModel] class.");
        }
        var ctor = t.GetConstructor( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes );
        if( ctor == null )
        {
            return (null, $"The configuration model '{t.FullName}' must have a parameterless constructor (public or not).");
        }
        building.Add( t );
        // The default values: a prototype (the required members are not checked by the run time).
        object prototype;
        try
        {
            prototype = ctor.Invoke( null );
        }
        catch( Exception ex )
        {
            return (null, $"Unable to create a default '{t.FullName}' configuration model: {(ex.InnerException ?? ex).Message}");
        }
        var nullability = new NullabilityInfoContext();
        var properties = new List<(ConfigurationPropertyDescription D, PropertyInfo P)>();
        var errors = new List<string>();
        foreach( var p in t.GetProperties( BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly ) )
        {
            if( p.GetIndexParameters().Length > 0 || p.SetMethod == null || !p.SetMethod.IsPublic ) continue;
            if( TryDescribe( t, p, prototype, nullability, building, out var d, out var e ) ) properties.Add( (d, p) );
            else errors.Add( e );
        }
        if( errors.Count > 0 ) return (null, string.Join( Environment.NewLine, errors ));
        var props = properties.Select( x => x.P ).ToArray();
        var description = new ConfigurationModelDescription( t,
                                                             GetDocumentation( t.Assembly, "T:" + DocName( t ) ),
                                                             properties.Select( x => x.D ).ToImmutableArray(),
                                                             values =>
                                                             {
                                                                 var m = ctor.Invoke( null );
                                                                 for( int i = 0; i < props.Length; i++ )
                                                                 {
                                                                     if( values.Has( i ) ) props[i].SetValue( m, values[i] );
                                                                 }
                                                                 return m;
                                                             } );
        return (description, null);
    }

    static bool TryDescribe( Type model,
                             PropertyInfo p,
                             object prototype,
                             NullabilityInfoContext nullability,
                             HashSet<Type> building,
                             [NotNullWhen( true )] out ConfigurationPropertyDescription? description,
                             [NotNullWhen( false )] out string? error )
    {
        description = null;
        error = null;
        var type = p.PropertyType;
        bool isNullable = nullability.Create( p ).WriteState == NullabilityState.Nullable;
        if( Nullable.GetUnderlyingType( type ) is Type u )
        {
            type = u;
            isNullable = true;
        }
        ConfigurationPropertyKind kind;
        Type? valueType = null;
        Type? modelType = null;
        Func<object, object>? createCollection = null;
        if( IsOpaque( type ) )
        {
            kind = ConfigurationPropertyKind.Opaque;
        }
        else if( IsBasic( type ) )
        {
            kind = ConfigurationPropertyKind.Value;
            valueType = type;
        }
        else if( IsModel( type ) )
        {
            kind = ConfigurationPropertyKind.Model;
            modelType = type;
        }
        else if( TryGetListItem( type, out var item ) && (IsBasic( item ) || IsModel( item )) )
        {
            kind = ConfigurationPropertyKind.List;
            if( IsModel( item ) ) modelType = item;
            else valueType = item;
            createCollection = o =>
            {
                var source = (IReadOnlyList<object?>)o;
                var a = Array.CreateInstance( item, source.Count );
                for( int i = 0; i < source.Count; i++ ) a.SetValue( source[i], i );
                return a;
            };
        }
        else if( TryGetDictionaryValue( type, out var value ) && (IsModel( value ) || IsBasic( value )) )
        {
            kind = ConfigurationPropertyKind.Dictionary;
            if( IsModel( value ) ) modelType = value;
            else valueType = value;
            var dictionaryType = typeof( Dictionary<,> ).MakeGenericType( typeof( string ), value );
            createCollection = o =>
            {
                var d = (IDictionary)Activator.CreateInstance( dictionaryType, StringComparer.OrdinalIgnoreCase )!;
                foreach( var e in (IReadOnlyList<KeyValuePair<string, object?>>)o ) d.Add( e.Key, e.Value );
                return d;
            };
        }
        else
        {
            error = $"The property '{p.Name}' of the configuration model '{model.FullName}' has an unsupported type '{p.PropertyType}': a basic value "
                    + "(string, bool, int, long, double, decimal, enum, TimeSpan, Guid, Uri, ConfigurationSecretValue), a [ConfigurationModel], an array or a read only list "
                    + "of them, a IReadOnlyDictionary<string, model or basic value>, or an IConfiguration is expected.";
            return false;
        }
        // A ConfigurationSecretValue is a secret: it says so.
        if( valueType == typeof( ConfigurationSecretValue ) && !p.IsDefined( typeof( ConfigurationSecretAttribute ), inherit: false ) )
        {
            error = $"The property '{p.Name}' of the configuration model '{model.FullName}' is a ConfigurationSecretValue: it must be a [ConfigurationSecret].";
            return false;
        }
        // A nested model must be valid too (its errors are the errors of this one).
        Func<ConfigurationModelDescription>? nested = null;
        if( modelType != null )
        {
            if( !building.Contains( modelType ) )
            {
                var (_, nestedError) = Build( modelType, building );
                if( nestedError != null )
                {
                    error = nestedError;
                    return false;
                }
            }
            var m = modelType;
            nested = () => { lock( _lock ) return _cache[m].D!; };
        }
        var current = p.GetValue( prototype );
        bool hasDefault = current != null && !(p.PropertyType.IsValueType && current.Equals( Activator.CreateInstance( p.PropertyType ) ));
        description = new ConfigurationPropertyDescription( p.Name,
                                                            kind,
                                                            valueType,
                                                            nested,
                                                            p.IsDefined( typeof( RequiredMemberAttribute ), inherit: false ),
                                                            isNullable,
                                                            hasDefault,
                                                            p.GetCustomAttribute<ConfigurationLevelAttribute>()?.Level ?? ConfigurationLevel.Init,
                                                            p.IsDefined( typeof( ConfigurationSecretAttribute ), inherit: false ),
                                                            p.IsDefined( typeof( ConfigurationPerDeploymentAttribute ), inherit: false ),
                                                            p.GetCustomAttribute<ConfigurationSchemaAttribute>()?.SchemaUri,
                                                            GetDocumentation( model.Assembly, "P:" + DocName( model ) + "." + p.Name ),
                                                            createCollection,
                                                            kind == ConfigurationPropertyKind.Value && hasDefault ? current : null,
                                                            p.IsDefined( typeof( ConfigurationNoDefaultsAttribute ), inherit: false ) );
        return true;
    }

    internal static bool IsModel( Type t ) => t.IsClass && t.IsDefined( typeof( ConfigurationModelAttribute ), inherit: false );

    internal static bool IsBasic( Type t ) => t.IsEnum
                                              || t == typeof( string ) || t == typeof( bool ) || t == typeof( int ) || t == typeof( long )
                                              || t == typeof( double ) || t == typeof( decimal )
                                              || t == typeof( TimeSpan ) || t == typeof( Guid ) || t == typeof( Uri )
                                              || t == typeof( ConfigurationSecretValue );

    internal static bool IsOpaque( Type t ) => t.FullName is "Microsoft.Extensions.Configuration.IConfiguration"
                                                          or "Microsoft.Extensions.Configuration.IConfigurationSection"
                                                          or "CK.Core.ImmutableConfigurationSection";

    internal static bool TryGetListItem( Type t, [NotNullWhen( true )] out Type? item )
    {
        if( t.IsArray && t.GetArrayRank() == 1 )
        {
            item = t.GetElementType()!;
            return true;
        }
        if( t.IsGenericType )
        {
            var def = t.GetGenericTypeDefinition();
            if( def == typeof( IReadOnlyList<> ) || def == typeof( IReadOnlyCollection<> ) || def == typeof( IEnumerable<> ) )
            {
                item = t.GetGenericArguments()[0];
                return true;
            }
        }
        item = null;
        return false;
    }

    internal static bool TryGetDictionaryValue( Type t, [NotNullWhen( true )] out Type? value )
    {
        if( t.IsGenericType && t.GetGenericArguments() is [var k, var v] && k == typeof( string ) )
        {
            var def = t.GetGenericTypeDefinition();
            if( def == typeof( IReadOnlyDictionary<,> ) || def == typeof( IDictionary<,> ) || def == typeof( Dictionary<,> ) )
            {
                value = v;
                return true;
            }
        }
        value = null;
        return false;
    }

    // The documentation identifier of a type: its full name, a nested type with a dot.
    static string DocName( Type t ) => t.FullName!.Replace( '+', '.' );

    // The <summary> of a member in the Xml documentation file next to the assembly, as plain text: a code reference is
    // its leaf name, <c> and <code> become ` markers, the other tags are removed, the white spaces are collapsed.
    static string? GetDocumentation( Assembly a, string memberId )
    {
        if( !_docs.TryGetValue( a, out var doc ) )
        {
            doc = null;
            try
            {
                if( !string.IsNullOrEmpty( a.Location ) )
                {
                    var path = Path.ChangeExtension( a.Location, ".xml" );
                    if( File.Exists( path ) ) doc = XDocument.Load( path );
                }
            }
            catch( Exception )
            {
                // No documentation: it is optional.
            }
            _docs[a] = doc;
        }
        var summary = doc?.Root?.Element( "members" )?.Elements( "member" )
                          .FirstOrDefault( m => (string?)m.Attribute( "name" ) == memberId )?.Element( "summary" );
        if( summary == null ) return null;
        var b = new StringBuilder();
        AppendText( b, summary );
        var text = Regex.Replace( b.ToString(), @"\s+", " " ).Trim();
        return text.Length == 0 ? null : text;
    }

    static void AppendText( StringBuilder b, XElement e )
    {
        foreach( var n in e.Nodes() )
        {
            if( n is XText t ) b.Append( t.Value );
            else if( n is XElement c )
            {
                switch( c.Name.LocalName )
                {
                    case "see":
                    case "seealso":
                        if( !c.IsEmpty ) AppendText( b, c );
                        else
                        {
                            var r = (string?)c.Attribute( "cref" ) ?? (string?)c.Attribute( "langword" ) ?? (string?)c.Attribute( "href" ) ?? "";
                            // "T:CK.Core.X" or "M:CK.Core.X.Y(System.Int32)": the leaf name.
                            int paren = r.IndexOf( '(' );
                            if( paren >= 0 ) r = r[..paren];
                            int colon = r.IndexOf( ':' );
                            if( colon >= 0 ) r = r[(colon + 1)..];
                            int dot = r.LastIndexOf( '.' );
                            b.Append( dot >= 0 ? r[(dot + 1)..] : r );
                        }
                        break;
                    case "paramref":
                    case "typeparamref":
                        b.Append( (string?)c.Attribute( "name" ) );
                        break;
                    case "c":
                    case "code":
                        b.Append( '`' );
                        AppendText( b, c );
                        b.Append( '`' );
                        break;
                    default:
                        AppendText( b, c );
                        break;
                }
            }
        }
    }
}
