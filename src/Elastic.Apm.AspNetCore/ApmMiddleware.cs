// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Tasks;
using Elastic.Apm.Api;
using Elastic.Apm.AspNetCore.Extensions;
using Elastic.Apm.Config;
using Elastic.Apm.DistributedTracing;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

[assembly:
	InternalsVisibleTo(
		"Elastic.Apm.Tests, PublicKey=002400000480000094000000060200000024000052534131000400000100010051df3e4d8341d66c6dfbf35b2fda3627d08073156ed98eef81122b94e86ef2e44e7980202d21826e367db9f494c265666ae30869fb4cd1a434d171f6b634aa67fa8ca5b9076d55dc3baa203d3a23b9c1296c9f45d06a45cf89520bef98325958b066d8c626db76dd60d0508af877580accdd0e9f88e46b6421bf09a33de53fe1")]
[assembly:
	InternalsVisibleTo(
		"Elastic.Apm.AspNetCore.Tests, PublicKey=002400000480000094000000060200000024000052534131000400000100010051df3e4d8341d66c6dfbf35b2fda3627d08073156ed98eef81122b94e86ef2e44e7980202d21826e367db9f494c265666ae30869fb4cd1a434d171f6b634aa67fa8ca5b9076d55dc3baa203d3a23b9c1296c9f45d06a45cf89520bef98325958b066d8c626db76dd60d0508af877580accdd0e9f88e46b6421bf09a33de53fe1")]
[assembly:
	InternalsVisibleTo(
		"Elastic.Apm.PerfTests, PublicKey=002400000480000094000000060200000024000052534131000400000100010051df3e4d8341d66c6dfbf35b2fda3627d08073156ed98eef81122b94e86ef2e44e7980202d21826e367db9f494c265666ae30869fb4cd1a434d171f6b634aa67fa8ca5b9076d55dc3baa203d3a23b9c1296c9f45d06a45cf89520bef98325958b066d8c626db76dd60d0508af877580accdd0e9f88e46b6421bf09a33de53fe1")]

namespace Elastic.Apm.AspNetCore
{
	// ReSharper disable once ClassNeverInstantiated.Global
	internal class ApmMiddleware
	{
		private readonly IApmLogger _logger;

		private readonly RequestDelegate _next;
		private readonly Tracer _tracer;
		private readonly IConfigurationReader _configurationReader;

		public ApmMiddleware(RequestDelegate next, Tracer tracer, IApmAgent agent)
		{
			_next = next;
			_tracer = tracer;
			_logger = agent.Logger.Scoped(nameof(ApmMiddleware));
			_configurationReader = agent.ConfigurationReader;
		}

		public async Task InvokeAsync(HttpContext context)
		{
			var transaction = StartTransactionAsync(context);

			if(transaction != null)
				await FillSampledTransactionContextRequest(transaction, context);

			try
			{
				await _next(context);
			}
			catch (Exception e) when (transaction != null)
			{
				transaction.CaptureException(e);
				// It'd be nice to have this in an exception filter, but that would force us capturing the request body synchronously.
				// Therefore we rather unwind the stack in the catch block and call the async method.
				if (context != null)
					await transaction.CollectRequestBodyAsync(true, context.Request, _logger, transaction.ConfigSnapshot);

				throw;
			}
			finally
			{
				// In case an error handler middleware is registered, the catch block above won't be executed, because the
				// error handler handles all the exceptions - in this case, based on the response code and the config, we may capture the body here
				if (transaction != null && transaction.IsContextCreated && context?.Response.StatusCode >= 400
					&& transaction.Context?.Request?.Body is string body
					&& (string.IsNullOrEmpty(body) || body == Apm.Consts.Redacted))
					await transaction.CollectRequestBodyAsync(true, context.Request, _logger, transaction.ConfigSnapshot);

				if(transaction != null)
					StopTransaction(transaction, context);
			}
		}

		private Transaction StartTransactionAsync(HttpContext context)
		{
			try
			{
				if (WildcardMatcher.IsAnyMatch(_configurationReader.TransactionIgnoreUrls, context.Request.Path))
				{
					_logger.Debug()?.Log("Request ignored based on TransactionIgnoreUrls, url: {urlPath}", context.Request.Path);
					return null;
				}

				Transaction transaction;
				var transactionName = $"{context.Request.Method} {context.Request.Path}";

				if (context.Request.Headers.ContainsKey(TraceContext.TraceParentHeaderNamePrefixed)
					|| context.Request.Headers.ContainsKey(TraceContext.TraceParentHeaderName))
				{
					var headerValue = context.Request.Headers.ContainsKey(TraceContext.TraceParentHeaderName)
						? context.Request.Headers[TraceContext.TraceParentHeaderName].ToString()
						: context.Request.Headers[TraceContext.TraceParentHeaderNamePrefixed].ToString();

					var tracingData = context.Request.Headers.ContainsKey(TraceContext.TraceStateHeaderName)
						? TraceContext.TryExtractTracingData(headerValue, context.Request.Headers[TraceContext.TraceStateHeaderName].ToString())
						: TraceContext.TryExtractTracingData(headerValue);

					if (tracingData != null)
					{
						_logger.Debug()
							?.Log(
								"Incoming request with {TraceParentHeaderName} header. DistributedTracingData: {DistributedTracingData}. Continuing trace.",
								TraceContext.TraceParentHeaderNamePrefixed, tracingData);

						transaction = _tracer.StartTransactionInternal(transactionName, ApiConstants.TypeRequest, tracingData);
					}
					else
					{
						_logger.Debug()
							?.Log(
								"Incoming request with invalid {TraceParentHeaderName} header (received value: {TraceParentHeaderValue}). Starting trace with new trace id.",
								TraceContext.TraceParentHeaderNamePrefixed, headerValue);

						transaction = _tracer.StartTransactionInternal(transactionName, ApiConstants.TypeRequest);
					}
				}
				else
				{
					_logger.Debug()?.Log("Incoming request. Starting Trace.");
					transaction = _tracer.StartTransactionInternal(transactionName, ApiConstants.TypeRequest);
				}

				return transaction;
			}
			catch (Exception ex)
			{
				_logger?.Error()?.LogException(ex, "Exception thrown while trying to start transaction");
				return null;
			}
		}

		private async Task FillSampledTransactionContextRequest(Transaction transaction, HttpContext context)
		{
			if (transaction.IsSampled) await FillSampledTransactionContextRequest(context, transaction);
		}

		private void StopTransaction(Transaction transaction, HttpContext context)
		{
			if (transaction == null) return;

			try
			{
				if (!transaction.HasCustomName)
				{
					//fixup Transaction.Name - e.g. /user/profile/1 -> /user/profile/{id}
					var routeData = context.GetRouteData()?.Values;

					if (routeData != null)
					{
						var name = GetNameFromRouteContext(routeData);

						if (!string.IsNullOrWhiteSpace(name)) transaction.Name = $"{context.Request.Method} {name}";
					}
				}

				transaction.Result = Transaction.StatusCodeToResult(GetProtocolName(context.Request.Protocol), context.Response.StatusCode);

				if (transaction.IsSampled)
				{
					FillSampledTransactionContextResponse(context, transaction);
					FillSampledTransactionContextUser(context, transaction);
				}
			}
			catch (Exception ex)
			{
				_logger?.Error()?.LogException(ex, "Exception thrown while trying to stop transaction");
			}
			finally
			{
				transaction.End();
			}
		}

		private string GetRawUrl(HttpRequest httpRequest)
		{
			var rawPathAndQuery = httpRequest.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
			return rawPathAndQuery == null ? null : UriHelper.BuildAbsolute(httpRequest.Scheme, httpRequest.Host, rawPathAndQuery);
		}

		private async Task FillSampledTransactionContextRequest(HttpContext context, Transaction transaction)
		{
			try
			{
				if (context?.Request == null) return;

				var url = new Url
				{
					Full = context.Request.GetEncodedUrl(),
					HostName = context.Request.Host.Host,
					Protocol = GetProtocolName(context.Request.Protocol),
					Raw = GetRawUrl(context.Request) ?? context.Request.GetEncodedUrl(),
					PathName = context.Request.Path,
					Search = context.Request.QueryString.Value.Length > 0 ? context.Request.QueryString.Value.Substring(1) : string.Empty
				};

				transaction.Context.Request = new Request(context.Request.Method, url)
				{
					Socket = new Socket { Encrypted = context.Request.IsHttps, RemoteAddress = context.Connection?.RemoteIpAddress?.ToString() },
					HttpVersion = GetHttpVersion(context.Request.Protocol),
					Headers = GetHeaders(context.Request.Headers, transaction.ConfigSnapshot)
				};

				await transaction.CollectRequestBodyAsync(false, context.Request, _logger, transaction.ConfigSnapshot);
			}
			catch (Exception ex)
			{
				// context.request is optional: https://github.com/elastic/apm-server/blob/64a4ab96ba138050fe496b17d31deb2cf8830deb/docs/spec/request.json#L5
				_logger?.Error()
					?.LogException(ex, "Exception thrown while trying to fill request context for sampled transaction {TransactionId}",
						transaction.Id);
			}
		}

		private Dictionary<string, string> GetHeaders(IHeaderDictionary headers, IConfigSnapshot configSnapshot) =>
			configSnapshot.CaptureHeaders && headers != null
				? headers.ToDictionary(header => header.Key, header => header.Value.ToString())
				: null;

		private void FillSampledTransactionContextResponse(HttpContext context, Transaction transaction)
		{
			try
			{
				transaction.Context.Response = new Response
				{
					Finished = context.Response.HasStarted, //TODO ?
					StatusCode = context.Response.StatusCode,
					Headers = GetHeaders(context.Response.Headers, transaction.ConfigSnapshot)
				};
			}
			catch (Exception ex)
			{
				// context.response is optional: https://github.com/elastic/apm-server/blob/64a4ab96ba138050fe496b17d31deb2cf8830deb/docs/spec/context.json#L16
				_logger?.Error()
					?.LogException(ex, "Exception thrown while trying to fill response context for sampled transaction {TransactionId}",
						transaction.Id);
			}
		}

		private void FillSampledTransactionContextUser(HttpContext context, Transaction transaction)
		{
			try
			{
				if (context.User?.Identity != null && context.User.Identity.IsAuthenticated && transaction.Context.User == null)
				{
					transaction.Context.User = new User
					{
						UserName = context.User.Identity.Name,
						Id = GetClaimWithFallbackValue(ClaimTypes.NameIdentifier, Consts.OpenIdClaimTypes.UserId),
						Email = GetClaimWithFallbackValue(ClaimTypes.Email, Consts.OpenIdClaimTypes.Email)
					};

					_logger.Debug()?.Log("Captured user - {CapturedUser}", transaction.Context.User);
				}
			}
			catch (Exception ex)
			{
				// context.user is optional: https://github.com/elastic/apm-server/blob/64a4ab96ba138050fe496b17d31deb2cf8830deb/docs/spec/user.json#L5
				_logger?.Error()
					?.LogException(ex, "Exception thrown while trying to fill user context for sampled transaction {TransactionId}",
						transaction.Id);
			}

			string GetClaimWithFallbackValue(string claimType, string fallbackClaimType)
			{
				var idClaims = context.User.Claims.Where(n => n.Type == claimType || n.Type == fallbackClaimType);
				var enumerable = idClaims.ToList();
				return enumerable.Any() ? enumerable.First().Value : string.Empty;
			}
		}

		//credit: https://github.com/Microsoft/ApplicationInsights-aspnetcore
		private static string GetNameFromRouteContext(IDictionary<string, object> routeValues)
		{
			string name = null;

			if (routeValues.Count <= 0) return null;

			routeValues.TryGetValue("controller", out var controller);
			var controllerString = controller == null ? string.Empty : controller.ToString();

			if (!string.IsNullOrEmpty(controllerString))
			{
				name = controllerString;

				routeValues.TryGetValue("action", out var action);
				var actionString = action == null ? string.Empty : action.ToString();

				if (!string.IsNullOrEmpty(actionString)) name += "/" + actionString;

				if (routeValues.Keys.Count <= 2) return name;

				// Add parameters
				var sortedKeys = routeValues.Keys
					.Where(key =>
						!string.Equals(key, "controller", StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(key, "action", StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(key, "!__route_group", StringComparison.OrdinalIgnoreCase))
					.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (sortedKeys.Length <= 0) return name;

				var arguments = string.Join(@"/", sortedKeys);
				name += " {" + arguments + "}";
			}
			else
			{
				routeValues.TryGetValue("page", out var page);
				var pageString = page == null ? string.Empty : page.ToString();
				if (!string.IsNullOrEmpty(pageString)) name = pageString;
			}

			return name;
		}

		[SuppressMessage("ReSharper", "PatternAlwaysOfType")]
		private static string GetProtocolName(string protocol)
		{
			switch (protocol)
			{
				case string s when string.IsNullOrEmpty(s):
					return string.Empty;
				case string s when s.StartsWith("HTTP", StringComparison.InvariantCulture): //in case of HTTP/2.x we only need HTTP
					return "HTTP";
				default:
					return protocol;
			}
		}

		private static string GetHttpVersion(string protocolString)
		{
			switch (protocolString)
			{
				case "HTTP/1.0":
					return "1.0";
				case "HTTP/1.1":
					return "1.1";
				case "HTTP/2.0":
					return "2.0";
				case null:
					return "unknown";
				default:
					return protocolString.Replace("HTTP/", string.Empty);
			}
		}
	}
}
