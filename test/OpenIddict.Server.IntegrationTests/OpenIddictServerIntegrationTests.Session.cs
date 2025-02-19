﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;
using static OpenIddict.Server.OpenIddictServerHandlers.Protection;

namespace OpenIddict.Server.IntegrationTests;

public abstract partial class OpenIddictServerIntegrationTests
{
    [Theory]
    [InlineData(nameof(HttpMethod.Delete))]
    [InlineData(nameof(HttpMethod.Head))]
    [InlineData(nameof(HttpMethod.Options))]
    [InlineData(nameof(HttpMethod.Put))]
    [InlineData(nameof(HttpMethod.Trace))]
    public async Task ExtractLogoutRequest_UnexpectedMethodReturnsAnError(string method)
    {
        // Arrange
        await using var server = await CreateServerAsync(options => options.EnableDegradedMode());
        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.SendAsync(method, "/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.GetResourceString(SR.ID2084), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2084), response.ErrorUri);
    }

    [Theory]
    [InlineData("custom_error", null, null)]
    [InlineData("custom_error", "custom_description", null)]
    [InlineData("custom_error", "custom_description", "custom_uri")]
    [InlineData(null, "custom_description", null)]
    [InlineData(null, "custom_description", "custom_uri")]
    [InlineData(null, null, "custom_uri")]
    [InlineData(null, null, null)]
    public async Task ExtractLogoutRequest_AllowsRejectingRequest(string error, string description, string uri)
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ExtractLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Reject(error, description, uri);

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal(error ?? Errors.InvalidRequest, response.Error);
        Assert.Equal(description, response.ErrorDescription);
        Assert.Equal(uri, response.ErrorUri);
    }

    [Fact]
    public async Task ExtractLogoutRequest_AllowsHandlingResponse()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ExtractLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Transaction.SetProperty("custom_response", new
                    {
                        name = "Bob le Bricoleur"
                    });

                    context.HandleRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.GetAsync("/connect/logout");

        // Assert
        Assert.Equal("Bob le Bricoleur", (string?) response["name"]);
    }

    [Fact]
    public async Task ExtractLogoutRequest_AllowsSkippingHandler()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ExtractLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SkipRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.GetAsync("/connect/logout");

        // Assert
        Assert.Equal("Bob le Magnifique", (string?) response["name"]);
    }

    [Theory]
    [InlineData("/path", SR.ID2030)]
    [InlineData("/tmp/file.xml", SR.ID2030)]
    [InlineData("C:\\tmp\\file.xml", SR.ID2030)]
    [InlineData("http://www.fabrikam.com/path#param=value", SR.ID2031)]
    public async Task ValidateLogoutRequest_InvalidRedirectUriCausesAnError(string uri, string message)
    {
        // Arrange
        await using var server = await CreateServerAsync();
        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = uri
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(string.Format(SR.GetResourceString(message), Parameters.PostLogoutRedirectUri), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(message), response.ErrorUri);
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsRejectedWhenClientCannotBeFound()
    {
        // Arrange
        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()))
                .ReturnsAsync(value: null);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            ClientId = "Fabrikam",
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.FormatID2052(Parameters.ClientId), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2052), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsRejectedWhenNoMatchingApplicationIsFound()
    {
        // Arrange
        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .Returns(AsyncEnumerable.Empty<OpenIddictApplication>());
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.FormatID2052(Parameters.PostLogoutRedirectUri), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2052), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsRejectedWhenPostLogoutRedirectUriForExplicitClientIsInvalid()
    {
        // Arrange
        var application = new OpenIddictApplication();

        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()))
                .ReturnsAsync(application);

            mock.Setup(manager => manager.ValidatePostLogoutRedirectUriAsync(application, "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.Configure(options => options.IgnoreEndpointPermissions = false);
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            ClientId = "Fabrikam",
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.FormatID2052(Parameters.PostLogoutRedirectUri), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2052), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Mock.Get(manager).Verify(manager => manager.ValidatePostLogoutRedirectUriAsync(application,
            "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsRejectedWhenPostLogoutRedirectUriForImplicitClientIsInvalid()
    {
        // Arrange
        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .Returns(AsyncEnumerable.Empty<OpenIddictApplication>());
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.Configure(options => options.IgnoreEndpointPermissions = false);
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.FormatID2052(Parameters.PostLogoutRedirectUri), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2052), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync(
            "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsRejectedWhenNoMatchingApplicationIsGrantedEndpointPermission()
    {
        // Arrange
        var applications = new[]
        {
            new OpenIddictApplication(),
            new OpenIddictApplication()
        };

        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .Returns(applications.ToAsyncEnumerable());

            mock.Setup(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            mock.Setup(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.Configure(options => options.IgnoreEndpointPermissions = false);
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.FormatID2052(Parameters.PostLogoutRedirectUri), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2052), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsValidatedWhenMatchingApplicationIsFound()
    {
        // Arrange
        var applications = new[]
        {
            new OpenIddictApplication(),
            new OpenIddictApplication(),
            new OpenIddictApplication()
        };

        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .Returns(applications.ToAsyncEnumerable());

            mock.Setup(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            mock.Setup(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            mock.Setup(manager => manager.HasPermissionAsync(applications[2], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            mock.Setup(manager => manager.ValidatePostLogoutRedirectUriAsync(applications[1], "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.SetLogoutEndpointUris("/signout");
            options.Configure(options => options.IgnoreEndpointPermissions = false);

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/signout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path",
            State = "af0ifjsldkj"
        });

        // Assert
        Assert.Equal("af0ifjsldkj", response.State);

        Mock.Get(manager).Verify(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(applications[2], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsRejectedWhenEndpointPermissionIsNotGranted()
    {
        // Arrange
        var application = new OpenIddictApplication();

        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()))
                .ReturnsAsync(application);

            mock.Setup(manager => manager.GetPostLogoutRedirectUrisAsync(application, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImmutableArray.Create("http://www.fabrikam.com/path"));

            mock.Setup(manager => manager.HasPermissionAsync(application,
                Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.Configure(options => options.IgnoreEndpointPermissions = false);
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            ClientId = "Fabrikam"
        });

        // Assert
        Assert.Equal(Errors.UnauthorizedClient, response.Error);
        Assert.Equal(SR.GetResourceString(SR.ID2140), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2140), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(application,
            Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ValidateLogoutRequest_InvalidIdentityTokenHintDoesNotCauseAnError()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    Assert.Null(context.IdentityTokenHintPrincipal);

                    context.SignOut();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            ClientId = "Fabrikam",
            IdTokenHint = "id_token",
            PostLogoutRedirectUri = "http://www.fabrikam.com/path",
            State = "af0ifjsldkj"
        });

        // Assert
        Assert.Equal("af0ifjsldkj", response.State);
    }

    [Fact]
    public async Task ValidateLogoutRequest_InvalidIdentityTokenHintCausesAnErrorWhenRejectionIsEnabled()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ProcessAuthenticationContext>(builder =>
            {
                builder.UseInlineHandler(context =>
                {
                    context.RejectIdentityToken = true;

                    return default;
                });

                builder.SetOrder(EvaluateValidatedTokens.Descriptor.Order + 500);
            });
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            ClientId = "Fabrikam",
            IdTokenHint = "id_token"
        });

        // Assert
        Assert.Equal(Errors.InvalidToken, response.Error);
        Assert.Equal(SR.GetResourceString(SR.ID2009), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2009), response.ErrorUri);
    }

    [Fact]
    public async Task ValidateLogoutRequest_IdentityTokenHintCausesAnErrorWhenExplicitCallerIsNotAuthorized()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();
            options.Configure(options => options.IgnoreEndpointPermissions = false);

            options.AddEventHandler<ValidateTokenContext>(builder =>
            {
                builder.UseInlineHandler(context =>
                {
                    Assert.Equal("id_token", context.Token);
                    Assert.Equal(new[] { TokenTypeHints.IdToken }, context.ValidTokenTypes);

                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"))
                        .SetTokenType(TokenTypeHints.IdToken)
                        .SetPresenters("Contoso")
                        .SetClaim(Claims.Subject, "Bob le Bricoleur");

                    return default;
                });

                builder.SetOrder(ValidateIdentityModelToken.Descriptor.Order - 500);
            });
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            ClientId = "Fabrikam",
            IdTokenHint = "id_token"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.GetResourceString(SR.ID2141), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2141), response.ErrorUri);
    }

    [Fact]
    public async Task ValidateLogoutRequest_IdentityTokenHintCausesAnErrorWhenInferredCallerIsNotAuthorized()
    {
        // Arrange
        var applications = new[]
        {
            new OpenIddictApplication(),
            new OpenIddictApplication()
        };

        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByPostLogoutRedirectUriAsync("http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .Returns(new[] { applications[0] }.ToAsyncEnumerable());

            mock.Setup(manager => manager.HasPermissionAsync(applications[0], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            mock.Setup(manager => manager.ValidatePostLogoutRedirectUriAsync(applications[0], "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            mock.Setup(manager => manager.GetClientIdAsync(applications[0], It.IsAny<CancellationToken>()))
                .ReturnsAsync("Fabrikam");

            mock.Setup(manager => manager.FindByClientIdAsync("Contoso", It.IsAny<CancellationToken>()))
                .ReturnsAsync(applications[1]);

            mock.Setup(manager => manager.HasPermissionAsync(applications[1], Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            mock.Setup(manager => manager.ValidatePostLogoutRedirectUriAsync(applications[1], "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.Configure(options => options.IgnoreEndpointPermissions = false);

            options.AddEventHandler<ValidateTokenContext>(builder =>
            {
                builder.UseInlineHandler(context =>
                {
                    Assert.Equal("id_token", context.Token);
                    Assert.Equal(new[] { TokenTypeHints.IdToken }, context.ValidTokenTypes);

                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"))
                        .SetTokenType(TokenTypeHints.IdToken)
                        .SetPresenters("Contoso")
                        .SetClaim(Claims.Subject, "Bob le Bricoleur");

                    return default;
                });

                builder.SetOrder(ValidateIdentityModelToken.Descriptor.Order - 500);
            });
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            IdTokenHint = "id_token",
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal(Errors.InvalidRequest, response.Error);
        Assert.Equal(SR.GetResourceString(SR.ID2141), response.ErrorDescription);
        Assert.Equal(SR.FormatID8000(SR.ID2141), response.ErrorUri);

        Mock.Get(manager).Verify(manager => manager.FindByClientIdAsync("Contoso", It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.ValidatePostLogoutRedirectUriAsync(applications[0], "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.ValidatePostLogoutRedirectUriAsync(applications[1], "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ValidateLogoutRequest_RequestIsValidatedWhenIdentityTokenHintIsExpired()
    {
        // Arrange
        var application = new OpenIddictApplication();

        var manager = CreateApplicationManager(mock =>
        {
            mock.Setup(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()))
                .ReturnsAsync(application);

            mock.Setup(manager => manager.ValidatePostLogoutRedirectUriAsync(application, "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            mock.Setup(manager => manager.HasPermissionAsync(application,
                Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        });

        await using var server = await CreateServerAsync(options =>
        {
            options.Services.AddSingleton(manager);

            options.SetLogoutEndpointUris("/signout");

            options.Configure(options => options.IgnoreEndpointPermissions = false);

            options.AddEventHandler<ValidateTokenContext>(builder =>
            {
                builder.UseInlineHandler(context =>
                {
                    Assert.Equal("id_token", context.Token);
                    Assert.Equal(new[] { TokenTypeHints.IdToken }, context.ValidTokenTypes);

                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"))
                        .SetTokenType(TokenTypeHints.IdToken)
                        .SetPresenters("Fabrikam")
                        .SetExpirationDate(new DateTimeOffset(2017, 1, 1, 0, 0, 0, TimeSpan.Zero))
                        .SetClaim(Claims.Subject, "Bob le Bricoleur");

                    return default;
                });

                builder.SetOrder(ValidateIdentityModelToken.Descriptor.Order - 500);
            });

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    Assert.Equal("Bob le Bricoleur", context.IdentityTokenHintPrincipal
                        ?.FindFirst(Claims.Subject)?.Value);

                    context.SignOut();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/signout", new OpenIddictRequest
        {
            ClientId = "Fabrikam",
            IdTokenHint = "id_token",
            PostLogoutRedirectUri = "http://www.fabrikam.com/path",
            State = "af0ifjsldkj"
        });

        // Assert
        Assert.Equal("af0ifjsldkj", response.State);

        Mock.Get(manager).Verify(manager => manager.FindByClientIdAsync("Fabrikam", It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Mock.Get(manager).Verify(manager => manager.ValidatePostLogoutRedirectUriAsync(application, "http://www.fabrikam.com/path", It.IsAny<CancellationToken>()), Times.Once());
        Mock.Get(manager).Verify(manager => manager.HasPermissionAsync(application, Permissions.Endpoints.Logout, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("custom_error", null, null)]
    [InlineData("custom_error", "custom_description", null)]
    [InlineData("custom_error", "custom_description", "custom_uri")]
    [InlineData(null, "custom_description", null)]
    [InlineData(null, "custom_description", "custom_uri")]
    [InlineData(null, null, "custom_uri")]
    [InlineData(null, null, null)]
    public async Task ValidateLogoutRequest_AllowsRejectingRequest(string error, string description, string uri)
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ValidateLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Reject(error, description, uri);

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal(error ?? Errors.InvalidRequest, response.Error);
        Assert.Equal(description, response.ErrorDescription);
        Assert.Equal(uri, response.ErrorUri);
    }

    [Fact]
    public async Task ValidateLogoutRequest_AllowsHandlingResponse()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ValidateLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Transaction.SetProperty("custom_response", new
                    {
                        name = "Bob le Bricoleur"
                    });

                    context.HandleRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal("Bob le Bricoleur", (string?) response["name"]);
    }

    [Fact]
    public async Task ValidateLogoutRequest_AllowsSkippingHandler()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<ValidateLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SkipRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal("Bob le Magnifique", (string?) response["name"]);
    }

    [Theory]
    [InlineData("custom_error", null, null)]
    [InlineData("custom_error", "custom_description", null)]
    [InlineData("custom_error", "custom_description", "custom_uri")]
    [InlineData(null, "custom_description", null)]
    [InlineData(null, "custom_description", "custom_uri")]
    [InlineData(null, null, "custom_uri")]
    [InlineData(null, null, null)]
    public async Task HandleLogoutRequest_AllowsRejectingRequest(string error, string description, string uri)
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Reject(error, description, uri);

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal(error ?? Errors.InvalidRequest, response.Error);
        Assert.Equal(description, response.ErrorDescription);
        Assert.Equal(uri, response.ErrorUri);
    }

    [Fact]
    public async Task HandleLogoutRequest_AllowsHandlingResponse()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Transaction.SetProperty("custom_response", new
                    {
                        name = "Bob le Bricoleur"
                    });

                    context.HandleRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal("Bob le Bricoleur", (string?) response["name"]);
    }

    [Fact]
    public async Task HandleLogoutRequest_AllowsSkippingHandler()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SkipRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal("Bob le Magnifique", (string?) response["name"]);
    }

    [Fact]
    public async Task HandleLogoutResponse_ResponseContainsCustomParameters()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    context.Parameters["custom_parameter"] = "custom_value";
                    context.Parameters["parameter_with_multiple_values"] = new[]
                    {
                        "custom_value_1",
                        "custom_value_2"
                    };

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal("custom_value", (string?) response["custom_parameter"]);
        Assert.Equal(new[] { "custom_value_1", "custom_value_2" }, (string[]?) response["parameter_with_multiple_values"]);
    }

    [Fact]
    public async Task ApplyLogoutResponse_AllowsHandlingResponse()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));

            options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Transaction.SetProperty("custom_response", new
                    {
                        name = "Bob le Bricoleur"
                    });

                    context.HandleRequest();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Equal("Bob le Bricoleur", (string?) response["name"]);
    }

    [Fact]
    public async Task ApplyLogoutResponse_ResponseContainsCustomParameters()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));

            options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Response["custom_parameter"] = "custom_value";
                    context.Response["parameter_with_multiple_values"] = new[]
                    {
                        "custom_value_1",
                        "custom_value_2"
                    };

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal("custom_value", (string?) response["custom_parameter"]);
        Assert.Equal(new[] { "custom_value_1", "custom_value_2" }, (string[]?) response["parameter_with_multiple_values"]);
    }

    [Fact]
    public async Task ApplyLogoutResponse_UsesPostLogoutRedirectUriWhenProvided()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));

            options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Response["target_uri"] = context.PostLogoutRedirectUri;

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path"
        });

        // Assert
        Assert.Equal("http://www.fabrikam.com/path", (string?) response["target_uri"]);
    }

    [Fact]
    public async Task ApplyLogoutResponse_ReturnsEmptyResponseWhenNoPostLogoutRedirectUriIsProvided()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));

            options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Response["target_uri"] = context.PostLogoutRedirectUri;

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/connect/logout", new OpenIddictRequest());

        // Assert
        Assert.Empty(response.GetParameters());
    }

    [Fact]
    public async Task ApplyLogoutResponse_DoesNotSetStateWhenUserIsNotRedirected()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();
            options.SetLogoutEndpointUris("/signout");

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/signout", new OpenIddictRequest
        {
            State = "af0ifjsldkj"
        });

        // Assert
        Assert.Null(response.State);
    }

    [Fact]
    public async Task ApplyLogoutResponse_FlowsStateWhenRedirectUriIsUsed()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();
            options.SetLogoutEndpointUris("/signout");

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/signout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path",
            State = "af0ifjsldkj"
        });

        // Assert
        Assert.Equal("af0ifjsldkj", response.State);
    }

    [Fact]
    public async Task ApplyLogoutResponse_DoesNotOverrideStateSetByApplicationCode()
    {
        // Arrange
        await using var server = await CreateServerAsync(options =>
        {
            options.EnableDegradedMode();
            options.SetLogoutEndpointUris("/signout");

            options.AddEventHandler<HandleLogoutRequestContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.SignOut();

                    return default;
                }));

            options.AddEventHandler<ApplyLogoutResponseContext>(builder =>
                builder.UseInlineHandler(context =>
                {
                    context.Response.State = "custom_state";

                    return default;
                }));
        });

        await using var client = await server.CreateClientAsync();

        // Act
        var response = await client.PostAsync("/signout", new OpenIddictRequest
        {
            PostLogoutRedirectUri = "http://www.fabrikam.com/path",
            State = "af0ifjsldkj"
        });

        // Assert
        Assert.Equal("custom_state", response.State);
    }
}
