using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CK.Core;

/// <summary>
/// Writes a model instance as a Markdown table (its keys and their values), for the documents that describe an
/// application (the "architecture.md" of a host). Uses reflection: for the tools and the App builder, not for the run time.
/// </summary>
public static class ConfigurationModelMarkdown
{
    static readonly JsonSerializerOptions _json = new JsonSerializerOptions { WriteIndented = false };

    /// <summary>
    /// Writes the table of a model: one row per property, its value, "(default)" when the configuration doesn't write it.
    /// A secret is never written (only its presence).
    /// </summary>
    /// <param name="b">The target builder.</param>
    /// <param name="description">The description of the model.</param>
    /// <param name="model">The model instance.</param>
    /// <param name="writtenKeys">The keys that the configuration writes (case insensitive). Null when unknown: no mark.</param>
    /// <returns>The builder.</returns>
    public static StringBuilder AppendTable( StringBuilder b,
                                             ConfigurationModelDescription description,
                                             object model,
                                             IEnumerable<string>? writtenKeys )
    {
        Throw.CheckNotNullArgument( b );
        Throw.CheckNotNullArgument( description );
        Throw.CheckNotNullArgument( model );
        var written = writtenKeys != null ? new HashSet<string>( writtenKeys, StringComparer.OrdinalIgnoreCase ) : null;
        b.Append( "| Key | Value |" ).AppendLine().Append( "|---|---|" ).AppendLine();
        foreach( var p in description.Properties )
        {
            var value = model.GetType().GetProperty( p.Name )?.GetValue( model );
            b.Append( "| " ).Append( p.Name ).Append( " | " ).Append( Escape( Format( p, value ) ) );
            if( written != null && !written.Contains( p.Name ) ) b.Append( " *(default)*" );
            b.Append( " |" ).AppendLine();
        }
        return b;
    }

    static string Format( ConfigurationPropertyDescription p, object? value )
    {
        if( p.IsSecret ) return value == null ? "*(none)*" : "*(secret)*";
        return value switch
        {
            null => "*(none)*",
            string s => $"`{s}`",
            Uri u => $"`{u}`",
            bool v => v ? "`true`" : "`false`",
            Enum e => $"`{e}`",
            IFormattable f when p.Kind == ConfigurationPropertyKind.Value => $"`{f.ToString( null, CultureInfo.InvariantCulture )}`",
            IEnumerable e when p.Kind == ConfigurationPropertyKind.List && p.Model == null
                => e.Cast<object?>().Any() ? string.Join( ", ", e.Cast<object?>().Select( v => $"`{Convert.ToString( v, CultureInfo.InvariantCulture )}`" ) ) : "*(empty)*",
            _ => $"`{JsonSerializer.Serialize( value, value.GetType(), _json )}`"
        };
    }

    // A table cell: no pipe, no new line.
    static string Escape( string s ) => s.Replace( "|", "\\|" ).Replace( "\r", "" ).Replace( "\n", " " );
}
