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
    public void the_description_is_built_by_reflection()
    {
        var d = ConfigurationModelDescription.Get( typeof( PasskeyModel ) );
        d.ModelType.ShouldBe( typeof( PasskeyModel ) );
        d.Documentation.ShouldBe( "The passkeys of a party (the shape of the Passkey option)." );
        d.Properties.Select( p => p.Name ).ShouldBe( ["RpId", "RpName", "Timeout"] );
        var rpId = d.Find( "rpid" ).ShouldNotBeNull();
        rpId.IsRequired.ShouldBeTrue();
        rpId.Documentation.ShouldBe( "Gets the relying party identifier: a domain name (`example.com` or `localhost`)." );
        d.Find( "RpName" )!.IsNullable.ShouldBeTrue();
        d.Find( "RpName" )!.Documentation.ShouldBe( "Gets the displayed name of the relying party. Defaults to the RpId." );
        d.Find( "Timeout" )!.HasDefault.ShouldBeTrue();
        d.Find( "Timeout" )!.DefaultValue.ShouldBe( TimeSpan.FromMinutes( 5 ) );
        d.Find( "RpName" )!.HasDefault.ShouldBeFalse();

        var root = ConfigurationModelDescription.Get( typeof( RootModel ) );
        // Built once per type.
        ConfigurationModelDescription.Get( typeof( RootModel ) ).ShouldBeSameAs( root );
        root.Find( "Count" )!.Level.ShouldBe( ConfigurationLevel.Run );
        root.Find( "Passkey" )!.Kind.ShouldBe( ConfigurationPropertyKind.Model );
        root.Find( "Passkey" )!.Model.ShouldBeSameAs( d );
        root.Find( "Mode" )!.HasDefault.ShouldBeFalse( "Off is the default of the enum: no initializer to tell." );
        root.Find( "Count" )!.DefaultValue.ShouldBe( 3 );
        root.Find( "Cultures" )!.Kind.ShouldBe( ConfigurationPropertyKind.List );
        root.Find( "Schemes" )!.Kind.ShouldBe( ConfigurationPropertyKind.Dictionary );
        root.Find( "PublicUrl" )!.IsPerDeployment.ShouldBeTrue();
        root.Find( "Kestrel" )!.Kind.ShouldBe( ConfigurationPropertyKind.Opaque );
        ConfigurationModelDescription.Get( typeof( SchemeModel ) ).Find( "ClientSecret" )!.IsSecret.ShouldBeTrue();
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
            { "Count": 7, "Passkey": { "RpName": "Mine" }, "Schemes": { "A": { "ClientId": "a2" } }, "Lifetimes": { "Password": "00:10:00" } }
            """ );
        var defaults = Section( "Root", """
            {
              "Mode": "strict",
              "Cultures": "fr, en",
              "Passkey": { "RpId": "example.com", "RpName": "Default" },
              "Schemes": { "A": { "Authority": "https://a.example.com", "ClientId": "a1" }, "B": { "Authority": "https://b.example.com", "ClientId": "b" } },
              "Kestrel": { "Endpoints": { "Http": { "Url": "http://localhost:5000" } } },
              "Lifetimes": { "Password": "00:01:00", "Weak": "0" }
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
        // A dictionary of values: entry by entry, the value of the nearest layer.
        m.Lifetimes.Count.ShouldBe( 2 );
        m.Lifetimes["password"].ShouldBe( TimeSpan.FromMinutes( 10 ) );
        m.Lifetimes["Weak"].ShouldBe( TimeSpan.Zero );
    }

    [Test]
    public void an_entry_of_a_dictionary_of_values_is_a_value()
    {
        var c = Collector();
        ConfigurationModelReader.TryRead<RootModel>( Section( "Root", """{ "Lifetimes": { "A": "soon", "B": { "X": 1 } } }""" ), "Root", c, out _ ).ShouldBeFalse();
        var errors = Errors( c );
        errors.Length.ShouldBe( 2 );
        errors.ShouldContain( e => e.StartsWith( "'Test:Root:Lifetimes:A' = 'soon': a time span" ) );
        errors.ShouldContain( e => e.StartsWith( "'Test:Root:Lifetimes:B' must be a value (a time span" ) && e.EndsWith( "not a section." ) );
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
        var schema = ConfigurationModelSchema.ToJsonSchema( ConfigurationModelDescription.Get( typeof( RootModel ) ) );
        TestHelper.Monitor.Info( schema.ToJsonString() );
        schema["$schema"]!.GetValue<string>().ShouldBe( ConfigurationModelSchema.Dialect );
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        var props = schema["properties"]!;
        props["Count"]!["type"]!.GetValue<string>().ShouldBe( "integer" );
        props["Count"]!["x-ck-level"]!.GetValue<string>().ShouldBe( "Run" );
        props["Mode"]!["enum"]!.AsArray().Select( n => n!.GetValue<string>() ).ShouldBe( ["Off", "Lax", "Strict"] );
        props["PublicUrl"]!["x-ck-perDeployment"]!.GetValue<bool>().ShouldBeTrue();
        props["Lifetimes"]!["additionalProperties"]!["x-ck-format"]!.GetValue<string>().ShouldBe( "timespan" );
        props["Kestrel"]!["$ref"]!.GetValue<string>().ShouldBe( "https://json.schemastore.org/appsettings.json#/definitions/kestrel" );
        props["Passkey"]!["$ref"]!.GetValue<string>().ShouldBe( "#/$defs/CK.ConfigurationModel.Tests.PasskeyModel" );
        var passkey = schema["$defs"]!["CK.ConfigurationModel.Tests.PasskeyModel"]!;
        passkey["required"]!.AsArray().Select( n => n!.GetValue<string>() ).ShouldBe( ["RpId"] );
        var scheme = schema["$defs"]!["CK.ConfigurationModel.Tests.SchemeModel"]!;
        scheme["properties"]!["ClientSecret"]!["x-ck-secret"]!.GetValue<bool>().ShouldBeTrue();
        scheme["properties"]!["Authority"]!["format"]!.GetValue<string>().ShouldBe( "uri" );
        var timeout = schema["$defs"]!["CK.ConfigurationModel.Tests.PasskeyModel"]!["properties"]!["Timeout"]!;
        timeout["x-ck-format"]!.GetValue<string>().ShouldBe( "timespan" );
        timeout["default"]!.GetValue<string>().ShouldBe( "00:05:00" );
    }

    [Test]
    public void the_description_is_found_or_the_error_says_why()
    {
        ConfigurationModelDescription.TryGet( typeof( PasskeyModel ), out var d, out var error ).ShouldBeTrue();
        d.ShouldBeSameAs( ConfigurationModelDescription.Get( typeof( PasskeyModel ) ) );
        error.ShouldBeNull();

        ConfigurationModelDescription.TryGet( typeof( ConfigurationModelTests ), out d, out error ).ShouldBeFalse();
        d.ShouldBeNull();
        error.ShouldContain( "must be a concrete [ConfigurationModel] class" );

        ConfigurationModelDescription.TryGet( typeof( UnsupportedModel ), out d, out error ).ShouldBeFalse();
        error.ShouldContain( "'Stream'" );
        error.ShouldContain( "unsupported type" );
    }
}
