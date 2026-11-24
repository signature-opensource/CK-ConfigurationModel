using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CK.Core;

/// <summary>
/// Reads a configuration model (see <see cref="ConfigurationModelAttribute"/>) from the layers of its section: the section
/// itself, then the sections of the levels above it that hold defaults (the <c>"Defaults"</c> of the AppIdentity
/// configuration), the nearest first.
/// <list type="bullet">
///   <item><b>Key by key:</b> each property is the value of the nearest layer that has its key; a nested model is read
///   key by key from the layers of its key; a dictionary entry by entry (the union of the entries of the layers), each
///   entry key by key; a list as a whole (the nearest layer that has it). A list of values also accepts a comma separated
///   string (as <c>ReadStringArray</c> of CK.Configuration).</item>
///   <item><b>Every error is collected</b> in the <see cref="UserMessageCollector"/>: an unknown key (in any layer), a
///   missing required key, a value that can't be converted (the invariant culture), then the
///   <see cref="IConfigurationModel.Validate(IConfigurationValidationContext)"/> of the model.</item>
///   <item><b>Fail closed:</b> when an error has been collected, there is no model.</item>
/// </list>
/// </summary>
public static class ConfigurationModelReader
{
    /// <summary>
    /// Reads a model from the layers of its section. Its description is built by reflection
    /// (<see cref="ConfigurationModelDescription.Get(Type)"/>): for the tools and the tests.
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="layers">The section, then the sections of the levels above it, the nearest first. A null (or empty) layer has no value.</param>
    /// <param name="path">The configuration path of the model's section (for the messages when no layer has the section).</param>
    /// <param name="collector">The collector of the messages.</param>
    /// <param name="model">The model, or null when an error has been collected.</param>
    /// <param name="reservedKeys">The keys of the section that are not the model's (the options of its owner): not unknown.</param>
    /// <returns>True on success, false on error (the errors are in the collector).</returns>
    public static bool TryRead<T>( IReadOnlyList<ImmutableConfigurationSection?> layers,
                                   string path,
                                   UserMessageCollector collector,
                                   out T? model,
                                   IEnumerable<string>? reservedKeys = null ) where T : class
    {
        var o = TryRead( ConfigurationModelDescription.Get( typeof( T ) ), layers, path, collector, reservedKeys );
        model = (T?)o;
        return o != null;
    }

    /// <summary>
    /// Reads a model from its section only (no layers above it).
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="section">The section. Null when the configuration has no section (the model has only its defaults).</param>
    /// <param name="path">The configuration path of the model's section.</param>
    /// <param name="collector">The collector of the messages.</param>
    /// <param name="model">The model, or null when an error has been collected.</param>
    /// <returns>True on success, false on error (the errors are in the collector).</returns>
    public static bool TryRead<T>( ImmutableConfigurationSection? section,
                                   string path,
                                   UserMessageCollector collector,
                                   out T? model ) where T : class
    {
        return TryRead( [section], path, collector, out model );
    }

    /// <summary>
    /// Reads a model from the layers of its section, by its description (the tools: the model type is not known at
    /// compile time).
    /// </summary>
    /// <param name="description">The model description.</param>
    /// <param name="layers">The section, then the sections of the levels above it, the nearest first.</param>
    /// <param name="path">The configuration path of the model's section.</param>
    /// <param name="collector">The collector of the messages.</param>
    /// <param name="reservedKeys">The keys of the section that are not the model's (the options of its owner): not unknown.</param>
    /// <returns>The model, or null when an error has been collected.</returns>
    public static object? TryRead( ConfigurationModelDescription description,
                                   IReadOnlyList<ImmutableConfigurationSection?> layers,
                                   string path,
                                   UserMessageCollector collector,
                                   IEnumerable<string>? reservedKeys = null )
    {
        Throw.CheckNotNullArgument( description );
        Throw.CheckNotNullArgument( layers );
        Throw.CheckNotNullArgument( collector );
        int errorCount = collector.ErrorCount;
        var reserved = reservedKeys != null ? new HashSet<string>( reservedKeys, StringComparer.OrdinalIgnoreCase ) : null;
        var model = ReadModel( description, layers, path, collector, reserved );
        if( model is IConfigurationModel validated && collector.ErrorCount == errorCount )
        {
            validated.Validate( new ValidationContext( collector, path ) );
        }
        return collector.ErrorCount == errorCount ? model : null;
    }

    static object? ReadModel( ConfigurationModelDescription d,
                              IReadOnlyList<ImmutableConfigurationSection?> layers,
                              string path,
                              UserMessageCollector collector,
                              HashSet<string>? reservedKeys = null )
    {
        // An unknown key in any layer is an error: a misspelled key is never silent.
        foreach( var layer in layers )
        {
            if( layer == null ) continue;
            foreach( var c in layer.GetChildren() )
            {
                if( d.Find( c.Key ) == null && reservedKeys?.Contains( c.Key ) != true )
                {
                    var keys = string.Join( ", ", d.Properties.Select( p => p.Name ) );
                    collector.Error( $"Unknown key '{c.Path}': the keys of '{layer.Path}' are {(keys.Length > 0 ? keys : "none")}." );
                }
            }
        }
        var values = new ConfigurationModelValues( d.Properties.Length );
        for( int i = 0; i < d.Properties.Length; i++ )
        {
            var p = d.Properties[i];
            var sub = layers.Select( l => l?.TryGetSection( p.Name ) ).ToArray();
            var nearest = Array.Find( sub, s => s != null );
            var propertyPath = nearest?.Path ?? $"{path}:{p.Name}";
            switch( p.Kind )
            {
                case ConfigurationPropertyKind.Value:
                    if( nearest == null ) break;
                    if( nearest.Value == null )
                    {
                        collector.Error( $"'{propertyPath}' must be a value ({DisplayName( p.ValueType! )}), not a section." );
                        continue;
                    }
                    if( TryConvert( nearest.Value, p.ValueType!, out var v ) ) values.Set( i, v );
                    else collector.Error( $"'{propertyPath}' = '{nearest.Value}': {DisplayName( p.ValueType! )} expected." );
                    continue;
                case ConfigurationPropertyKind.Model:
                    if( nearest == null ) break;
                    var m = ReadModel( p.Model!, sub, propertyPath, collector );
                    if( m != null ) values.Set( i, m );
                    continue;
                case ConfigurationPropertyKind.List:
                    if( nearest == null ) break;
                    var items = ReadList( p, nearest, collector );
                    if( items != null ) values.Set( i, p.CreateCollection!( items ) );
                    continue;
                case ConfigurationPropertyKind.Dictionary:
                    if( nearest == null ) break;
                    var entries = ReadDictionary( p, sub, collector );
                    values.Set( i, p.CreateCollection!( entries ) );
                    continue;
                case ConfigurationPropertyKind.Opaque:
                    if( nearest == null ) break;
                    values.Set( i, nearest );
                    continue;
            }
            // No layer has the key.
            if( p.IsRequired ) collector.Error( $"'{propertyPath}' is required." );
        }
        return d.Create( values );
    }

    static List<object?>? ReadList( ConfigurationPropertyDescription p, ImmutableConfigurationSection section, UserMessageCollector collector )
    {
        var result = new List<object?>();
        bool success = true;
        if( section.Value != null )
        {
            // A list of values may be a comma separated string.
            if( p.Model != null )
            {
                collector.Error( $"'{section.Path}' must be a list of sections." );
                return null;
            }
            foreach( var s in section.Value.Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ) )
            {
                if( TryConvert( s, p.ValueType!, out var v ) ) result.Add( v );
                else
                {
                    collector.Error( $"'{section.Path}' contains '{s}': {DisplayName( p.ValueType! )} expected." );
                    success = false;
                }
            }
            return success ? result : null;
        }
        foreach( var item in section.GetChildren().OrderBy( c => int.TryParse( c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var n ) ? n : int.MaxValue ) )
        {
            if( p.Model != null )
            {
                var m = ReadModel( p.Model, [item], item.Path, collector );
                if( m == null ) success = false;
                result.Add( m );
            }
            else if( item.Value != null && TryConvert( item.Value, p.ValueType!, out var v ) )
            {
                result.Add( v );
            }
            else
            {
                collector.Error( $"'{item.Path}' = '{item.Value}': {DisplayName( p.ValueType! )} expected." );
                success = false;
            }
        }
        return success ? result : null;
    }

    static List<KeyValuePair<string, object?>> ReadDictionary( ConfigurationPropertyDescription p,
                                                               ImmutableConfigurationSection?[] layers,
                                                               UserMessageCollector collector )
    {
        // The union of the entries of the layers, in the order of their first appearance (the nearest layer first).
        var names = new List<string>();
        foreach( var l in layers )
        {
            if( l == null ) continue;
            foreach( var c in l.GetChildren() )
            {
                if( !names.Contains( c.Key, StringComparer.OrdinalIgnoreCase ) ) names.Add( c.Key );
            }
        }
        var result = new List<KeyValuePair<string, object?>>( names.Count );
        foreach( var name in names )
        {
            var entryLayers = layers.Select( l => l?.TryGetSection( name ) ).ToArray();
            var nearest = Array.Find( entryLayers, s => s != null )!;
            var m = ReadModel( p.Model!, entryLayers, nearest.Path, collector );
            result.Add( new KeyValuePair<string, object?>( name, m ) );
        }
        return result;
    }

    /// <summary>
    /// Converts a configuration text to a value of a basic type (the invariant culture).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="type">The value type (not a nullable).</param>
    /// <param name="value">The value.</param>
    /// <returns>True on success, false when the text is not a value of the type.</returns>
    public static bool TryConvert( string text, Type type, out object? value )
    {
        var inv = CultureInfo.InvariantCulture;
        value = null;
        if( type == typeof( string ) ) { value = text; return true; }
        if( type == typeof( bool ) ) { if( bool.TryParse( text, out var b ) ) value = b; }
        else if( type == typeof( int ) ) { if( int.TryParse( text, NumberStyles.Integer, inv, out var i ) ) value = i; }
        else if( type == typeof( long ) ) { if( long.TryParse( text, NumberStyles.Integer, inv, out var l ) ) value = l; }
        else if( type == typeof( double ) ) { if( double.TryParse( text, NumberStyles.Float, inv, out var f ) ) value = f; }
        else if( type == typeof( decimal ) ) { if( decimal.TryParse( text, NumberStyles.Number, inv, out var m ) ) value = m; }
        else if( type == typeof( TimeSpan ) ) { if( TimeSpan.TryParse( text, inv, out var t ) ) value = t; }
        else if( type == typeof( Guid ) ) { if( Guid.TryParse( text, out var g ) ) value = g; }
        else if( type == typeof( Uri ) ) { if( Uri.TryCreate( text, UriKind.RelativeOrAbsolute, out var u ) ) value = u; }
        else if( type.IsEnum )
        {
            // By name only: a number is not a name.
            if( !int.TryParse( text, out _ ) && Enum.TryParse( type, text, ignoreCase: true, out var e ) ) value = e;
        }
        else
        {
            Throw.NotSupportedException( $"Unsupported configuration value type '{type}'." );
        }
        return value != null;
    }

    // The context of IConfigurationModel.Validate: the messages go to the collector of the reading.
    sealed class ValidationContext : IConfigurationValidationContext
    {
        readonly UserMessageCollector _collector;

        public ValidationContext( UserMessageCollector collector, string path )
        {
            _collector = collector;
            Path = path;
        }

        public string Path { get; }

        public void Error( string message ) => _collector.Error( message );

        public void Warn( string message ) => _collector.Warn( message );
    }

    static string DisplayName( Type t ) => t.IsEnum
                                            ? $"one of {string.Join( ", ", Enum.GetNames( t ) )}"
                                            : t == typeof( TimeSpan ) ? "a time span (\"00:05:00\", \"1.00:00:00\")" : $"a {t.Name}";
}
