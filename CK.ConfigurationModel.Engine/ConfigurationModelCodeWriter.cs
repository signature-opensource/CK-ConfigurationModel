using CK.CodeGen;
using CK.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CK.Setup;

/// <summary>
/// Generates, in the code of an engine, the description of configuration models and their creation without reflection:
/// an <c>[UnsafeAccessor]</c> calls the parameterless constructor of the model (the <c>required</c> members are checked
/// by the reader, not by the run time) and the <c>init</c> setters of the read properties (the others keep their
/// initializer).
/// <para>
/// Each model is a nested static class of the host type (once per model, its nested models included), whose
/// <c>Description</c> is the <see cref="ConfigurationModelDescription"/> that the generated code gives to the reader.
/// </para>
/// </summary>
public sealed class ConfigurationModelCodeWriter
{
    readonly ITypeScope _host;
    readonly Dictionary<Type, string> _classes;

    /// <summary>
    /// Initializes a new writer.
    /// </summary>
    /// <param name="host">The generated type that hosts the classes of the models.</param>
    public ConfigurationModelCodeWriter( ITypeScope host )
    {
        Throw.CheckNotNullArgument( host );
        _host = host;
        _classes = new Dictionary<Type, string>();
    }

    /// <summary>
    /// Ensures that the code of a model is generated and gets the C# expression of its description (in the host type).
    /// </summary>
    /// <param name="monitor">The monitor (the errors).</param>
    /// <param name="modelType">The model type.</param>
    /// <param name="expression">The expression of its <see cref="ConfigurationModelDescription"/>.</param>
    /// <returns>True on success, false on error.</returns>
    public bool TryGetDescription( IActivityMonitor monitor, Type modelType, [NotNullWhen( true )] out string? expression )
    {
        Throw.CheckNotNullArgument( monitor );
        Throw.CheckNotNullArgument( modelType );
        expression = null;
        if( !TryGetClass( monitor, modelType, out var name ) ) return false;
        expression = name + ".Description";
        return true;
    }

    bool TryGetClass( IActivityMonitor monitor, Type modelType, [NotNullWhen( true )] out string? name )
    {
        if( _classes.TryGetValue( modelType, out name ) ) return true;
        if( !ConfigurationModelDescription.TryGet( modelType, out var d, out var error ) )
        {
            monitor.Error( error );
            return false;
        }
        // The generated code names the model and the types of its properties.
        var hidden = d.Properties.Select( p => p.ValueType ).Append( modelType ).FirstOrDefault( t => t != null && !t.IsVisible );
        if( hidden != null )
        {
            monitor.Error( $"The configuration model '{modelType:N}' must be public, as the types of its properties ('{hidden:N}' is not)." );
            return false;
        }
        name = "CKConfigurationModel" + _classes.Count;
        // Registered before its nested models: a model that contains itself refers to its own class.
        _classes.Add( modelType, name );
        var model = modelType.ToGlobalTypeName();
        var props = modelType.GetProperties( BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly );
        var c = _host.CreateType( $"static class {name}" );
        c.Append( "[global::System.Runtime.CompilerServices.UnsafeAccessor( global::System.Runtime.CompilerServices.UnsafeAccessorKind.Constructor )]" ).NewLine()
         .Append( "static extern " ).Append( model ).Append( " New();" ).NewLine();
        var create = new StringBuilder();
        create.Append( "static v => { var m = New(); " );
        var descriptions = new StringBuilder();
        for( int i = 0; i < d.Properties.Length; i++ )
        {
            var p = d.Properties[i];
            var propertyType = props.Single( x => x.Name == p.Name ).PropertyType.ToGlobalTypeName();
            c.Append( "[global::System.Runtime.CompilerServices.UnsafeAccessor( global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"set_" )
             .Append( p.Name ).Append( "\" )]" ).NewLine()
             .Append( "static extern void Set" ).Append( i.ToString() ).Append( "( " ).Append( model ).Append( " m, " ).Append( propertyType ).Append( " value );" ).NewLine();
            create.Append( "if( v.Has( " ).Append( i ).Append( " ) ) Set" ).Append( i ).Append( "( m, (" ).Append( propertyType ).Append( ")v[" ).Append( i ).Append( "]! ); " );
            string nested = "null";
            string createCollection = "null";
            var nestedType = p.Model?.ModelType;
            if( nestedType != null )
            {
                if( !TryGetClass( monitor, nestedType, out var nestedClass ) ) return false;
                nested = $"static () => {nestedClass}.Description";
            }
            if( p.Kind == ConfigurationPropertyKind.List )
            {
                var item = (nestedType ?? p.ValueType!).ToGlobalTypeName();
                createCollection = "static o => global::System.Linq.Enumerable.ToArray( global::System.Linq.Enumerable.Select( "
                                   + "(global::System.Collections.Generic.IReadOnlyList<object?>)o, x => (" + item + ")x! ) )";
            }
            else if( p.Kind == ConfigurationPropertyKind.Dictionary )
            {
                var value = (nestedType ?? p.ValueType!).ToGlobalTypeName();
                createCollection = "static o => { var d = new global::System.Collections.Generic.Dictionary<string, " + value + ">( global::System.StringComparer.OrdinalIgnoreCase ); "
                                   + "foreach( var e in (global::System.Collections.Generic.IReadOnlyList<global::System.Collections.Generic.KeyValuePair<string, object?>>)o ) d.Add( e.Key, (" + value + ")e.Value! ); return d; }";
            }
            if( i > 0 ) descriptions.Append( "," );
            descriptions.AppendLine()
                        .Append( "new global::CK.Core.ConfigurationPropertyDescription( " ).Append( Literal( p.Name ) )
                        .Append( ", global::CK.Core.ConfigurationPropertyKind." ).Append( p.Kind )
                        .Append( ", " ).Append( p.ValueType != null ? $"typeof( {p.ValueType.ToGlobalTypeName()} )" : "null" )
                        .Append( ", " ).Append( nested )
                        .Append( ", " ).Append( Bool( p.IsRequired ) ).Append( ", " ).Append( Bool( p.IsNullable ) ).Append( ", " ).Append( Bool( p.HasDefault ) )
                        .Append( ", global::CK.Core.ConfigurationLevel." ).Append( p.Level )
                        .Append( ", " ).Append( Bool( p.IsSecret ) ).Append( ", " ).Append( Bool( p.IsPerDeployment ) )
                        .Append( ", " ).Append( Literal( p.SchemaUri ) ).Append( ", " ).Append( Literal( p.Documentation ) )
                        // The default value is for the JSON Schema of the tools (built by reflection): not at run time.
                        .Append( ", " ).Append( createCollection )
                        .Append( p.IsNoDefaults ? ", isNoDefaults: true" : "" ).Append( " )" );
        }
        create.Append( "return m; }" );
        c.Append( "static global::CK.Core.ConfigurationModelDescription? _d;" ).NewLine()
         .Append( "public static global::CK.Core.ConfigurationModelDescription Description => _d ??= new global::CK.Core.ConfigurationModelDescription( typeof( " )
         .Append( model ).Append( " ), " ).Append( Literal( d.Documentation ) ).Append( "," ).NewLine()
         .Append( "global::System.Collections.Immutable.ImmutableArray.Create<global::CK.Core.ConfigurationPropertyDescription>(" )
         .Append( descriptions.ToString() ).Append( " )," ).NewLine()
         .Append( create.ToString() ).Append( " );" ).NewLine();
        return true;
    }

    static string Bool( bool b ) => b ? "true" : "false";

    static string Literal( string? s )
    {
        if( s == null ) return "null";
        var b = new StringBuilder( s.Length + 2 ).Append( '"' );
        foreach( var ch in s )
        {
            switch( ch )
            {
                case '"': b.Append( "\\\"" ); break;
                case '\\': b.Append( "\\\\" ); break;
                case '\n': b.Append( "\\n" ); break;
                case '\r': b.Append( "\\r" ); break;
                case '\t': b.Append( "\\t" ); break;
                default:
                    if( char.IsControl( ch ) ) b.Append( "\\u" ).Append( ((int)ch).ToString( "x4" ) );
                    else b.Append( ch );
                    break;
            }
        }
        return b.Append( '"' ).ToString();
    }
}
