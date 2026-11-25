using CK.CodeGen;
using CK.Core;
using CK.Setup;
using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using static CK.Testing.MonitorTestHelper;

namespace CK.ConfigurationModel.Tests;

[TestFixture]
public class CodeWriterTests
{
    static UserMessageCollector Collector() => new UserMessageCollector( new CurrentCultureInfo( new TranslationService(), NormalizedCultureInfo.CodeDefault ) );

    static ImmutableConfigurationSection Section( string key, string json ) => ImmutableConfigurationSection.CreateFromJson( "Test", key, json );

    [Test]
    public void the_generated_description_creates_the_models_without_reflection()
    {
        var workspace = CodeWorkspace.Create();
        workspace.EnsureAssemblyReference( typeof( RootModel ) );
        workspace.EnsureAssemblyReference( typeof( ConfigurationModelDescription ) );
        workspace.EnsureAssemblyReference( typeof( System.Collections.Immutable.ImmutableArray ) );
        var host = workspace.Global.FindOrCreateNamespace( "CK._g" ).CreateType( "public static class Host" );
        var writer = new ConfigurationModelCodeWriter( host );
        writer.TryGetDescription( TestHelper.Monitor, typeof( RootModel ), out var root ).ShouldBeTrue();
        // Once per model: the nested PasskeyModel and SchemeModel are already generated.
        writer.TryGetDescription( TestHelper.Monitor, typeof( PasskeyModel ), out var passkey ).ShouldBeTrue();
        host.Append( "public static global::CK.Core.ConfigurationModelDescription Root => " ).Append( root! ).Append( ";" ).NewLine()
            .Append( "public static global::CK.Core.ConfigurationModelDescription Passkey => " ).Append( passkey! ).Append( ";" ).NewLine();
        writer.TryGetDescription( TestHelper.Monitor, typeof( UnsupportedModel ), out _ ).ShouldBeFalse();

        var path = Path.Combine( TestHelper.TestProjectFolder, "bin", "Generated", $"CodeWriter{Guid.NewGuid():N}.dll" );
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        var r = new CK.CodeGen.CodeGenerator( CodeWorkspace.Factory ).Generate( workspace, path, skipCompilation: false, loader: Assembly.LoadFrom );
        r.LogResult( TestHelper.Monitor );
        r.Success.ShouldBeTrue();
        var hostType = r.Assembly!.GetType( "CK._g.Host" ).ShouldNotBeNull();
        var d = (ConfigurationModelDescription)hostType.GetProperty( "Root" )!.GetValue( null )!;
        var p = (ConfigurationModelDescription)hostType.GetProperty( "Passkey" )!.GetValue( null )!;
        // The same description as the reflection (but the default values that are for the schema).
        var reflected = ConfigurationModelDescription.Get( typeof( RootModel ) );
        d.Properties.Select( x => (x.Name, x.Kind, x.IsRequired, x.IsNullable, x.Level, x.IsSecret, x.IsPerDeployment, x.SchemaUri) )
         .ShouldBe( reflected.Properties.Select( x => (x.Name, x.Kind, x.IsRequired, x.IsNullable, x.Level, x.IsSecret, x.IsPerDeployment, x.SchemaUri) ) );
        d.Find( "Passkey" )!.Model.ShouldBeSameAs( p );

        // A model read with the generated description: only RpId is configured, the defaults are kept (the
        // required member is set by the reader, not by a constructor).
        var c = Collector();
        var m = (PasskeyModel?)ConfigurationModelReader.TryRead( p, [Section( "Passkey", """{ "RpId": "example.com" }""" )], "Test:Passkey", c );
        m.ShouldNotBeNull();
        m.RpId.ShouldBe( "example.com" );
        m.Timeout.ShouldBe( TimeSpan.FromMinutes( 5 ) );

        var root2 = (RootModel?)ConfigurationModelReader.TryRead( d,
                                                                  [Section( "Root", """{ "Count": "7", "Cultures": "fr, en", "Passkey": { "RpId": "x.org" }, "Schemes": { "A": { "Authority": "https://a", "ClientId": "a" } }, "Lifetimes": { "A": "00:00:30" } }""" )],
                                                                  "Test:Root",
                                                                  c );
        root2.ShouldNotBeNull();
        root2.Count.ShouldBe( 7 );
        root2.Cultures.ShouldBe( ["fr", "en"] );
        root2.Passkey!.RpId.ShouldBe( "x.org" );
        root2.Schemes["a"].ClientId.ShouldBe( "a" );
        root2.Lifetimes["a"].ShouldBe( TimeSpan.FromSeconds( 30 ) );

        var root3 = (RootModel?)ConfigurationModelReader.TryRead( d, [Section( "Root", """{ "ConnectionString": { "$SECRET": "Back:Db" }, "Databases": { "h": "x" } }""" )], "Test:Root", c );
        root3.ShouldNotBeNull();
        root3.ConnectionString!.Value.SecretName.ShouldBe( "Back:Db" );
        root3.Databases["h"].Value.ShouldBe( "x" );
    }
}
