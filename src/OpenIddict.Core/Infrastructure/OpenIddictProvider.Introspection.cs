﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Server;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenIddict.Infrastructure {
    public partial class OpenIddictProvider<TUser, TApplication, TAuthorization, TScope, TToken> : OpenIdConnectServerProvider
        where TUser : class where TApplication : class where TAuthorization : class where TScope : class where TToken : class {
        public override async Task ValidateIntrospectionRequest([NotNull] ValidateIntrospectionRequestContext context) {
            var services = context.HttpContext.RequestServices.GetRequiredService<OpenIddictServices<TUser, TApplication, TAuthorization, TScope, TToken>>();

            // Note: ASOS supports both GET and POST introspection requests but OpenIddict only accepts POST requests.
            if (!string.Equals(context.HttpContext.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "Introspection requests must use HTTP POST.");

                return;
            }

            // Note: ASOS supports unauthenticated introspection requests but OpenIddict uses
            // a stricter policy preventing unauthenticated/public applications from using
            // the introspection endpoint, as required by the specifications.
            // See https://tools.ietf.org/html/rfc7662 for more information.
            if (string.IsNullOrEmpty(context.ClientId) || string.IsNullOrEmpty(context.ClientSecret)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "Clients must be authenticated to use the introspection endpoint.");

                return;
            }

            // Retrieve the application details corresponding to the requested client_id.
            var application = await services.Applications.FindByIdAsync(context.ClientId);
            if (application == null) {
                services.Logger.LogError("The introspection request was rejected because the client " +
                                         "application was not found: '{ClientId}'.", context.ClientId);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidClient,
                    description: "Application not found in the database: ensure that your client_id is correct.");

                return;
            }

            // Reject non-confidential applications.
            var type = await services.Applications.GetClientTypeAsync(application);
            if (!string.Equals(type, OpenIddictConstants.ClientTypes.Confidential, StringComparison.OrdinalIgnoreCase)) {
                services.Logger.LogError("The introspection request was rejected because the public application " +
                                         "'{ClientId}' was not allowed to use this endpoint.", context.ClientId);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidClient,
                    description: "Public applications are not allowed to use the introspection endpoint.");

                return;
            }

            // Validate the client credentials.
            if (!await services.Applications.ValidateSecretAsync(application, context.ClientSecret)) {
                services.Logger.LogError("The introspection request was rejected because the confidential application " +
                                         "'{ClientId}' didn't specify valid client credentials.", context.ClientId);

                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidClient,
                    description: "Invalid credentials: ensure that you specified a correct client_secret.");

                return;
            }

            context.Validate();
        }

        public override async Task HandleIntrospectionRequest([NotNull] HandleIntrospectionRequestContext context) {
            var services = context.HttpContext.RequestServices.GetRequiredService<OpenIddictServices<TUser, TApplication, TAuthorization, TScope, TToken>>();

            var user = await services.Users.GetUserAsync(context.Ticket.Principal);
            if (user == null) {
                services.Logger.LogInformation("The token {Identifier} was declared as inactive because the " +
                                               "corresponding user ({Username}) was not found in the database.",
                                               context.Ticket.GetTicketId(), services.Users.GetUserName(context.Ticket.Principal));

                context.Active = false;

                return;
            }

            // When the received ticket is a refresh token, ensure it is still valid.
            if (context.Ticket.IsRefreshToken()) {
                // Retrieve the token from the database using the unique identifier stored in the refresh token:
                // if the corresponding entry cannot be found, return Active = false to indicate that is is no longer valid.
                var token = await services.Tokens.FindByIdAsync(context.Ticket.GetTicketId());
                if (token == null) {
                    services.Logger.LogInformation("The token {Identifier} was declared as inactive because " +
                                                   "it was revoked.", context.Ticket.GetTicketId());

                    context.Active = false;

                    return;
                }
            }
        }
    }
}