using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CK.ConfigurationModel.Generator;

/// <summary>
/// Completes each <c>[ConfigurationModel]</c> partial class: its description (the static <c>ModelDescription</c> of
/// <c>IDescribedConfigurationModel&lt;TSelf&gt;</c>) and the private constructor that sets the read values (the properties
/// that are not read keep their initializer). The collections are created by generated code: no reflection.
/// </summary>
[Generator( LanguageNames.CSharp )]
public sealed class ConfigurationModelGenerator : IIncrementalGenerator
{
    const string _modelAttribute = "CK.Core.ConfigurationModelAttribute";

    static readonly DiagnosticDescriptor _notPartial = new DiagnosticDescriptor(
        "CKCM001", "A configuration model must be partial",
        "The configuration model '{0}' must be a partial class (its description is generated)",
        "CK.ConfigurationModel", DiagnosticSeverity.Error, isEnabledByDefault: true );

    static readonly DiagnosticDescriptor _unsupported = new DiagnosticDescriptor(
        "CKCM002", "Unsupported configuration property type",
        "The property '{0}' of the configuration model '{1}' has an unsupported type '{2}': a basic value (string, bool, int, long, double, decimal, enum, TimeSpan, Guid, Uri), a [ConfigurationModel], an array or a read only list of them, a IReadOnlyDictionary<string, model>, or an IConfiguration is expected",
        "CK.ConfigurationModel", DiagnosticSeverity.Error, isEnabledByDefault: true );

    static readonly DiagnosticDescriptor _nested = new DiagnosticDescriptor(
        "CKCM003", "A configuration model must be a top level class",
        "The configuration model '{0}' must be a top level class (not nested in another type)",
        "CK.ConfigurationModel", DiagnosticSeverity.Error, isEnabledByDefault: true );

    sealed class Output
    {
        public string? HintName;
        public string? Source;
        public List<Diagnostic> Diagnostics = new List<Diagnostic>();
    }

    /// <inheritdoc />
    public void Initialize( IncrementalGeneratorInitializationContext context )
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            _modelAttribute,
            static ( node, _ ) => node is ClassDeclarationSyntax,
            static ( ctx, _ ) => Generate( (INamedTypeSymbol)ctx.TargetSymbol, (ClassDeclarationSyntax)ctx.TargetNode ) );
        context.RegisterSourceOutput( models, static ( spc, o ) =>
        {
            foreach( var d in o.Diagnostics ) spc.ReportDiagnostic( d );
            if( o.Source != null ) spc.AddSource( o.HintName!, o.Source );
        } );
    }

    static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
                                                        .AddMiscellaneousOptions( SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier );

    static Output Generate( INamedTypeSymbol model, ClassDeclarationSyntax declaration )
    {
        var o = new Output();
        if( model.ContainingType != null )
        {
            o.Diagnostics.Add( Diagnostic.Create( _nested, declaration.Identifier.GetLocation(), model.Name ) );
            return o;
        }
        if( !model.DeclaringSyntaxReferences.All( r => r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any( SyntaxKind.PartialKeyword ) ) )
        {
            o.Diagnostics.Add( Diagnostic.Create( _notPartial, declaration.Identifier.GetLocation(), model.Name ) );
            return o;
        }
        var self = model.ToDisplayString( SymbolDisplayFormat.FullyQualifiedFormat );
        var props = new StringBuilder();
        var assigns = new StringBuilder();
        int index = 0;
        bool success = true;
        foreach( var p in model.GetMembers().OfType<IPropertySymbol>() )
        {
            if( p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public || p.SetMethod == null ) continue;
            var decl = p.DeclaringSyntaxReferences.Select( r => r.GetSyntax() ).OfType<PropertyDeclarationSyntax>().FirstOrDefault();
            if( !TryDescribe( p, decl, out var description ) )
            {
                o.Diagnostics.Add( Diagnostic.Create( _unsupported, decl?.Identifier.GetLocation() ?? declaration.Identifier.GetLocation(),
                                                      p.Name, model.Name, p.Type.ToDisplayString() ) );
                success = false;
                continue;
            }
            if( props.Length > 0 ) props.Append( "," ).AppendLine();
            props.Append( "            " ).Append( description );
            assigns.Append( "        if( v.Has( " ).Append( index ).Append( " ) ) " ).Append( p.Name )
                   .Append( " = (" ).Append( p.Type.ToDisplayString( _typeFormat ) ).Append( ")v[" ).Append( index ).AppendLine( "]!;" );
            index++;
        }
        if( !success ) return o;

        bool hasExplicitConstructor = model.InstanceConstructors.Any( c => !c.IsImplicitlyDeclared );
        var b = new StringBuilder();
        b.AppendLine( "// <auto-generated/> by CK.ConfigurationModel.Generator." );
        b.AppendLine( "#nullable enable" );
        if( !model.ContainingNamespace.IsGlobalNamespace )
        {
            b.Append( "namespace " ).Append( model.ContainingNamespace.ToDisplayString() ).AppendLine( ";" );
        }
        b.Append( "partial class " ).Append( model.Name ).Append( " : global::CK.Core.IDescribedConfigurationModel<" ).Append( self ).AppendLine( ">" );
        b.AppendLine( "{" );
        b.AppendLine( "    static global::CK.Core.ConfigurationModelDescription? _ckModelDescription;" );
        b.AppendLine();
        b.AppendLine( "    /// <summary>" );
        b.AppendLine( "    /// Gets the description of this configuration model (generated)." );
        b.AppendLine( "    /// </summary>" );
        b.AppendLine( "    public static global::CK.Core.ConfigurationModelDescription ModelDescription => _ckModelDescription ??=" );
        b.Append( "        new global::CK.Core.ConfigurationModelDescription( typeof( " ).Append( self ).Append( " ), " )
         .Append( Literal( GetDocumentation( declaration ) ) ).AppendLine( "," );
        b.AppendLine( "            global::System.Collections.Immutable.ImmutableArray.Create<global::CK.Core.ConfigurationPropertyDescription>(" );
        b.Append( props.ToString() ).AppendLine( " )," );
        b.Append( "            static v => new " ).Append( self ).AppendLine( "( v ) );" );
        b.AppendLine();
        // A required member that is not read is an error of the reader: the model is then never returned (fail closed).
        b.AppendLine( "#pragma warning disable CS8618" );
        b.AppendLine( "    [global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]" );
        b.Append( "    private " ).Append( model.Name ).AppendLine( "( global::CK.Core.ConfigurationModelValues v )" );
        b.AppendLine( "    {" );
        b.Append( assigns.ToString() );
        b.AppendLine( "    }" );
        b.AppendLine( "#pragma warning restore CS8618" );
        if( !hasExplicitConstructor )
        {
            b.AppendLine();
            b.AppendLine( "    /// <summary>" );
            b.AppendLine( "    /// Initializes a new model with its default values." );
            b.AppendLine( "    /// </summary>" );
            b.Append( "    public " ).Append( model.Name ).AppendLine( "() { }" );
        }
        b.AppendLine( "}" );
        o.HintName = model.ToDisplayString().Replace( '<', '{' ).Replace( '>', '}' ) + ".ConfigurationModel.g.cs";
        o.Source = b.ToString();
        return o;
    }

    static bool IsModel( ITypeSymbol t ) => t is INamedTypeSymbol n && n.GetAttributes().Any( a => a.AttributeClass?.ToDisplayString() == _modelAttribute );

    static bool IsBasic( ITypeSymbol t )
    {
        if( t.TypeKind == TypeKind.Enum ) return true;
        switch( t.SpecialType )
        {
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return true;
        }
        var name = t.ToDisplayString();
        return name == "System.TimeSpan" || name == "System.Guid" || name == "System.Uri";
    }

    static bool IsOpaque( ITypeSymbol t )
    {
        var name = t.WithNullableAnnotation( NullableAnnotation.NotAnnotated ).ToDisplayString();
        return name == "Microsoft.Extensions.Configuration.IConfiguration"
               || name == "Microsoft.Extensions.Configuration.IConfigurationSection"
               || name == "CK.Core.ImmutableConfigurationSection";
    }

    static string TypeOf( ITypeSymbol t ) => "typeof( " + t.WithNullableAnnotation( NullableAnnotation.NotAnnotated ).ToDisplayString( SymbolDisplayFormat.FullyQualifiedFormat ) + " )";

    static bool TryDescribe( IPropertySymbol p, PropertyDeclarationSyntax? decl, out string description )
    {
        description = string.Empty;
        var type = p.Type;
        bool isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        if( type is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T )
        {
            type = nt.TypeArguments[0];
            isNullable = true;
        }
        string kind;
        string valueType = "null";
        string model = "null";
        string createCollection = "null";
        if( IsOpaque( type ) )
        {
            kind = "Opaque";
        }
        else if( IsBasic( type ) )
        {
            kind = "Value";
            valueType = TypeOf( type );
        }
        else if( IsModel( type ) )
        {
            kind = "Model";
            model = "static () => " + type.WithNullableAnnotation( NullableAnnotation.NotAnnotated ).ToDisplayString( SymbolDisplayFormat.FullyQualifiedFormat ) + ".ModelDescription";
        }
        else if( TryGetListItem( type, out var item ) && (IsBasic( item ) || IsModel( item )) )
        {
            kind = "List";
            var itemName = item.WithNullableAnnotation( NullableAnnotation.NotAnnotated ).ToDisplayString( SymbolDisplayFormat.FullyQualifiedFormat );
            if( IsModel( item ) ) model = "static () => " + itemName + ".ModelDescription";
            else valueType = TypeOf( item );
            createCollection = "static o => global::System.Linq.Enumerable.ToArray( global::System.Linq.Enumerable.Select( (global::System.Collections.Generic.IReadOnlyList<object?>)o, x => (" + itemName + ")x! ) )";
        }
        else if( TryGetDictionaryValue( type, out var value ) && IsModel( value ) )
        {
            kind = "Dictionary";
            var valueName = value.WithNullableAnnotation( NullableAnnotation.NotAnnotated ).ToDisplayString( SymbolDisplayFormat.FullyQualifiedFormat );
            model = "static () => " + valueName + ".ModelDescription";
            createCollection = "static o => { var d = new global::System.Collections.Generic.Dictionary<string, " + valueName + ">( global::System.StringComparer.OrdinalIgnoreCase ); "
                               + "foreach( var e in (global::System.Collections.Generic.IReadOnlyList<global::System.Collections.Generic.KeyValuePair<string, object?>>)o ) d.Add( e.Key, (" + valueName + ")e.Value! ); return d; }";
        }
        else
        {
            return false;
        }
        string level = "global::CK.Core.ConfigurationLevel.Init";
        bool isSecret = false, isPerDeployment = false;
        string? schemaUri = null;
        foreach( var a in p.GetAttributes() )
        {
            switch( a.AttributeClass?.ToDisplayString() )
            {
                case "CK.Core.ConfigurationLevelAttribute":
                    if( a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Type is INamedTypeSymbol e )
                    {
                        var field = e.GetMembers().OfType<IFieldSymbol>().FirstOrDefault( f => f.HasConstantValue && Equals( f.ConstantValue, a.ConstructorArguments[0].Value ) );
                        if( field != null ) level = "global::CK.Core.ConfigurationLevel." + field.Name;
                    }
                    break;
                case "CK.Core.ConfigurationSecretAttribute": isSecret = true; break;
                case "CK.Core.ConfigurationPerDeploymentAttribute": isPerDeployment = true; break;
                case "CK.Core.ConfigurationSchemaAttribute":
                    schemaUri = a.ConstructorArguments.Length == 1 ? a.ConstructorArguments[0].Value as string : null;
                    break;
            }
        }
        bool hasDefault = decl?.Initializer != null;
        description = "new global::CK.Core.ConfigurationPropertyDescription( " + Literal( p.Name )
                      + ", global::CK.Core.ConfigurationPropertyKind." + kind
                      + ", " + valueType + ", " + model
                      + ", " + Bool( p.IsRequired ) + ", " + Bool( isNullable ) + ", " + Bool( hasDefault )
                      + ", " + level + ", " + Bool( isSecret ) + ", " + Bool( isPerDeployment )
                      + ", " + Literal( schemaUri ) + ", " + Literal( decl != null ? GetDocumentation( decl ) : null )
                      + ", " + createCollection + " )";
        return true;
    }

    static bool TryGetListItem( ITypeSymbol t, out ITypeSymbol item )
    {
        if( t is IArrayTypeSymbol a && a.Rank == 1 )
        {
            item = a.ElementType;
            return true;
        }
        if( t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 1 )
        {
            var def = n.OriginalDefinition.ToDisplayString();
            if( def == "System.Collections.Generic.IReadOnlyList<T>"
                || def == "System.Collections.Generic.IReadOnlyCollection<T>"
                || def == "System.Collections.Generic.IEnumerable<T>" )
            {
                item = n.TypeArguments[0];
                return true;
            }
        }
        item = null!;
        return false;
    }

    static bool TryGetDictionaryValue( ITypeSymbol t, out ITypeSymbol value )
    {
        if( t is INamedTypeSymbol n && n.IsGenericType && n.TypeArguments.Length == 2
            && n.TypeArguments[0].SpecialType == SpecialType.System_String )
        {
            var def = n.OriginalDefinition.ToDisplayString();
            if( def == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                || def == "System.Collections.Generic.IDictionary<TKey, TValue>"
                || def == "System.Collections.Generic.Dictionary<TKey, TValue>" )
            {
                value = n.TypeArguments[1];
                return true;
            }
        }
        value = null!;
        return false;
    }

    static string Bool( bool b ) => b ? "true" : "false";

    static string Literal( string? s ) => s == null ? "null" : SymbolDisplay.FormatLiteral( s, quote: true );

    // The <summary> of the Xml documentation of a declaration, as plain text: a code reference is its leaf name, <c> and
    // <code> become ` markers, the other tags are removed, the white spaces are collapsed. The /// lines are read as
    // text: without a documentation file, the compiler doesn't parse them (they are plain comments).
    static string? GetDocumentation( MemberDeclarationSyntax declaration )
    {
        var lines = Regex.Matches( declaration.GetLeadingTrivia().ToFullString(), @"^\s*///(.*)$", RegexOptions.Multiline )
                         .Cast<Match>()
                         .Select( m => m.Groups[1].Value.TrimEnd( '\r' ) );
        var summary = Regex.Match( string.Join( "\n", lines ), @"<summary>(.*?)</summary>", RegexOptions.Singleline );
        if( !summary.Success ) return null;
        var text = summary.Groups[1].Value;
        text = Regex.Replace( text, @"<see\s+(?:cref|langword|href)\s*=\s*""(?:[A-Z]:)?(?:[^""]*\.)?([^"".]+)""\s*/>", "$1" );
        text = Regex.Replace( text, @"<see\s+[^>]*>(.*?)</see>", "$1", RegexOptions.Singleline );
        text = Regex.Replace( text, @"<(?:paramref|typeparamref)\s+name\s*=\s*""([^""]*)""\s*/>", "$1" );
        text = Regex.Replace( text, @"</?(?:c|code)>", "`" );
        text = Regex.Replace( text, @"<[^>]+>", "" );
        text = text.Replace( "&lt;", "<" ).Replace( "&gt;", ">" ).Replace( "&amp;", "&" );
        text = Regex.Replace( text, @"\s+", " " ).Trim();
        return text.Length == 0 ? null : text;
    }
}
