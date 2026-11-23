using CK.Core;
using NUnit.Framework;
using Shouldly;
using System;
using System.Linq;
using static CK.Testing.MonitorTestHelper;

namespace CK.ConfigurationModel.Tests;

[TestFixture]
public class ConfigurationModelTests
{
    static UserMessageCollector Collector() => new UserMessageCollector( new CurrentCultureInfo( new TranslationService(), NormalizedCultureInfo.CodeDefault ) );

    static ImmutableConfigurationSection Section( string key, string json ) => ImmutableConfigurationSection.CreateFromJson( "Test", key, json );

    static string[] Errors( UserMessageCollector c ) => c.UserMessages.Where( m => m.Level == UserMessageLevel.Error ).Select( m => m.Text ).ToArray();

    [Test]
    public void the_description_is_generated()
    {
        var d = PasskeyModel.ModelDescription;
        d.ModelType.ShouldBe( typeof( PasskeyModel ) );
        d.Documentation.ShouldBe( "The passkeys of a party (the shape of the Passkey option)." );
        d.Properties.Select( p => p.Name ).ShouldBe( ["RpId", "RpName", "Timeout"] );
        var rpId = d.Find( "rpid" ).ShouldNotBeNull();
        rpId.IsRequired.ShouldBeTrue();
        rpId.Documentation.ShouldBe( "Gets the relying party identifier: a domain name (`example.com` or `localhost`)." );
        d.Find( "RpName" )!.IsNullable.ShouldBeTrue();
        d.Find( "RpName" )!.Documentation.ShouldBe( "Gets the displayed name of the relying party. Defaults to the RpId." );
        d.Find( "Timeout" )!.HasDefault.ShouldBeTrue();

        var root = RootModel.ModelDescription;
        root.Find( "Count" )!.Level.ShouldBe( ConfigurationLevel.Run );
        root.Find( "Passkey" )!.Kind.ShouldBe( ConfigurationPropertyKind.Model );
        root.Find( "Passkey" )!.Model.ShouldBe( PasskeyModel.ModelDescription );
        root.Find( "Cultures" )!.Kind.ShouldBe( ConfigurationPropertyKind.List );
        root.Find( "Schemes" )!.Kind.ShouldBe( ConfigurationPropertyKind.Dictionary );
        root.Find( "PublicUrl" )!.IsPerDeployment.ShouldBeTrue();
        root.Find( "Kestrel" )!.Kind.ShouldBe( ConfigurationPropertyKind.Opaque );
        SchemeModel.ModelDescription.Find( "ClientSecret" )!.IsSecret.ShouldBeTrue();
    }

    [Test]
    public void a_model_is_read_and_its_defaults_kept()
    {
        var c = Collector();
        ConfigurationModelReader.TryRead<PasskeyModel>( Section( "Passkey", """{ "RpId": "Example.COM" }""" ), "Test:Passkey", c, out var m ).ShouldBeTrue();
        m.ShouldNotBeNull();
        m.RpId.ShouldBe( "Example.COM" );
        m.RpName.ShouldBeNull();
        m.Timeout.ShouldBe( TimeSpan.FromMinutes( 5 ) );
        // The derived member: the configured value stays as written.
        m.EffectiveRpName.ShouldBe( "example.com" );
    }

    [Test]
    public void every_error_is_collected_and_fails_closed()
    {
        var c = Collector();
        ConfigurationModelReader.TryRead<PasskeyModel>( Section( "Passkey", """{ "RpName": "X", "Timeout": "soon", "Nope": 1 }""" ), "Test:Passkey", c, out var m ).ShouldBeFalse();
        m.ShouldBeNull();
        Errors( c ).ShouldBe( ["Unknown key 'Test:Passkey:Nope': the keys of 'Test:Passkey' are RpId, RpName, Timeout.",
                               "'Test:Passkey:RpId' is required.",
                               "'Test:Passkey:Timeout' = 'soon': a time span (\"00:05:00\", \"1.00:00:00\") expected."], ignoreOrder: true );
    }

    [Test]
    public void the_reserved_keys_are_not_unknown()
    {
        // The options of the owner of a model are keys of its section.
        var c = Collector();
        ConfigurationModelReader.TryRead<PasskeyModel>( [Section( "Passkey", """{ "RpId": "example.com", "MyOption": true }""" )], "Test:Passkey", c, out var m, ["MyOption"] ).ShouldBeTrue();
        m.ShouldNotBeNull();
        Errors( c ).ShouldBeEmpty();
    }

    [Test]
    public void Validate_runs_once_the_keys_are_read()
    {
        var c = Collector();
        ConfigurationModelReader.TryRead<PasskeyModel>( Section( "Passkey", """{ "RpId": "http://x:80", "Timeout": "00:20:00" }""" ), "Test:Passkey", c, out var m ).ShouldBeFalse();
        Errors( c ).ShouldBe( ["'Test:Passkey:RpId' = 'http://x:80': a domain name is expected.", "'Test:Passkey:Timeout': at most 10 minutes."] );
    }

    [Test]
    public void the_values_are_read_key_by_key_along_the_layers()
    {
        // The party's own section, then its "Defaults" (the nearest first).
        var party = Section( "Root", """
            { "Count": 7, "Passkey": { "RpName": "Mine" }, "Schemes": { "A": { "ClientId": "a2" } } }
            """ );
        var defaults = Section( "Root", """
            {
              "Mode": "strict",
              "Cultures": "fr, en",
              "Passkey": { "RpId": "example.com", "RpName": "Default" },
              "Schemes": { "A": { "Authority": "https://a.example.com", "ClientId": "a1" }, "B": { "Authority": "https://b.example.com", "ClientId": "b" } },
              "Kestrel": { "Endpoints": { "Http": { "Url": "http://localhost:5000" } } }
            }
            """ );
        var c = Collector();
        ConfigurationModelReader.TryRead<RootModel>( [party, defaults], "Root", c, out var m ).ShouldBeTrue( string.Join( ", ", Errors( c ) ) );
        m.ShouldNotBeNull();
        m.Count.ShouldBe( 7 );
        m.Mode.ShouldBe( Mode.Strict );
        m.Cultures.ShouldBe( ["fr", "en"] );
        m.Passkey.ShouldNotBeNull().RpId.ShouldBe( "example.com" );
        m.Passkey.RpName.ShouldBe( "Mine" );
        m.Schemes.Keys.ShouldBe( ["A", "B"], ignoreOrder: true );
        m.Schemes["a"].ClientId.ShouldBe( "a2" );
        m.Schemes["A"].Authority.ShouldBe( new Uri( "https://a.example.com" ) );
        m.Kestrel.ShouldNotBeNull()["Endpoints:Http:Url"].ShouldBe( "http://localhost:5000" );
    }

    [Test]
    public void a_model_without_section_has_its_defaults_or_misses_its_required_keys()
    {
        var c = Collector();
        ConfigurationModelReader.TryRead<RootModel>( (ImmutableConfigurationSection?)null, "Root", c, out var root ).ShouldBeTrue();
        root.ShouldNotBeNull().Count.ShouldBe( 3 );
        root.Cultures.ShouldBeEmpty();
        ConfigurationModelReader.TryRead<PasskeyModel>( (ImmutableConfigurationSection?)null, "Root:Passkey", c, out _ ).ShouldBeFalse();
        Errors( c ).ShouldBe( ["'Root:Passkey:RpId' is required."] );
    }

    [Test]
    public void the_JSON_Schema()
    {
        var schema = ConfigurationModelSchema.ToJsonSchema( RootModel.ModelDescription );
        TestHelper.Monitor.Info( schema.ToJsonString() );
        schema["$schema"]!.GetValue<string>().ShouldBe( ConfigurationModelSchema.Dialect );
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        var props = schema["properties"]!;
        props["Count"]!["type"]!.GetValue<string>().ShouldBe( "integer" );
        props["Count"]!["x-ck-level"]!.GetValue<string>().ShouldBe( "Run" );
        props["Mode"]!["enum"]!.AsArray().Select( n => n!.GetValue<string>() ).ShouldBe( ["Off", "Lax", "Strict"] );
        props["PublicUrl"]!["x-ck-perDeployment"]!.GetValue<bool>().ShouldBeTrue();
        props["Kestrel"]!["$ref"]!.GetValue<string>().ShouldBe( "https://json.schemastore.org/appsettings.json#/definitions/kestrel" );
        props["Passkey"]!["$ref"]!.GetValue<string>().ShouldBe( "#/$defs/CK.ConfigurationModel.Tests.PasskeyModel" );
        var passkey = schema["$defs"]!["CK.ConfigurationModel.Tests.PasskeyModel"]!;
        passkey["required"]!.AsArray().Select( n => n!.GetValue<string>() ).ShouldBe( ["RpId"] );
        var scheme = schema["$defs"]!["CK.ConfigurationModel.Tests.SchemeModel"]!;
        scheme["properties"]!["ClientSecret"]!["x-ck-secret"]!.GetValue<bool>().ShouldBeTrue();
        scheme["properties"]!["Authority"]!["format"]!.GetValue<string>().ShouldBe( "uri" );
    }
}
