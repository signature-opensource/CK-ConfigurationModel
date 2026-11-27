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
}
