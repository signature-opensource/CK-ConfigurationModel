using CK.Core;
using NUnit.Framework;
using Shouldly;
using System;
using System.Text;

namespace CK.ConfigurationModel.Tests;

[TestFixture]
public class ConfigurationModelMarkdownTests
{
    [Test]
    public void a_model_is_a_table_with_its_defaults_marked()
    {
        var b = new StringBuilder();
        ConfigurationModelMarkdown.AppendTable( b,
                                                ConfigurationModelDescription.Get( typeof( PasskeyModel ) ),
                                                new PasskeyModel { RpId = "example.com" },
                                                ["rpid"] );
        b.ToString().ReplaceLineEndings( "\n" ).ShouldBe( """
            | Key | Value |
            |---|---|
            | RpId | `example.com` |
            | RpName | *(none)* *(default)* |
            | Timeout | `00:05:00` *(default)* |

            """ );
    }

    [Test]
    public void a_secret_is_never_written()
    {
        var b = new StringBuilder();
        ConfigurationModelMarkdown.AppendTable( b,
                                                ConfigurationModelDescription.Get( typeof( SchemeModel ) ),
                                                new SchemeModel { Authority = new Uri( "https://idp.example.com" ), ClientId = "a|b", ClientSecret = "S3cr3t" },
                                                null );
        var text = b.ToString();
        text.ShouldNotContain( "S3cr3t" );
        text.ShouldContain( "| ClientSecret | *(secret)* |" );
        text.ShouldContain( "| Authority | `https://idp.example.com/` |" );
        text.ShouldContain( @"| ClientId | `a\|b` |", customMessage: "A pipe is escaped." );
    }

    [Test]
    public void a_secret_value_shows_its_handle_only()
    {
        var d = ConfigurationModelDescription.Get( typeof( RootModel ) );
        var b = new StringBuilder();
        ConfigurationModelMarkdown.AppendTable( b, d, new RootModel { ConnectionString = ConfigurationSecretValue.FromHandle( "Db" ) }, null );
        b.ToString().ShouldContain( "| ConnectionString | `$SECRET:Db` |" );
        b.Clear();
        ConfigurationModelMarkdown.AppendTable( b, d, new RootModel { ConnectionString = ConfigurationSecretValue.FromValue( "Password=P4ss" ) }, null );
        b.ToString().ShouldNotContain( "P4ss", customMessage: "A secret value is never written." );
        b.ToString().ShouldContain( "| ConnectionString | *(secret)* |" );
        // A nested model that holds a secret is not written as JSON.
        b.Clear();
        var schemes = new System.Collections.Generic.Dictionary<string, SchemeModel>
        {
            ["Test"] = new SchemeModel { Authority = new Uri( "https://idp.example.com" ), ClientId = "c", ClientSecret = "S3cr3t" }
        };
        ConfigurationModelMarkdown.AppendTable( b, d, new RootModel { Schemes = schemes }, null );
        b.ToString().ShouldNotContain( "S3cr3t" );
        b.ToString().ShouldContain( "| Schemes | *(contains secrets)* |" );
    }
}
