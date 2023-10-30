﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MiniWebServer.Abstractions;
using MiniWebServer.MiniApp;
using MiniWebServer.Mvc.Abstraction;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace MiniWebServer.Mvc
{
    public class MvcMiddleware : IMiddleware
    {
        // https://github.com/daohainam/mini-web-server/issues/6

        private readonly IServiceCollection serviceCollection;
        private readonly ILogger<MvcMiddleware> logger;
        private readonly IActionFinder actionFinder;
        private readonly IViewEngine viewEngine;

        public MvcMiddleware(MvcOptions options, ILoggerFactory loggerFactory, IServiceCollection serviceCollection)
        {
            ArgumentNullException.ThrowIfNull(nameof(options));
            this.serviceCollection = serviceCollection ?? throw new ArgumentNullException(nameof(serviceCollection));

            logger = loggerFactory != null ? loggerFactory.CreateLogger<MvcMiddleware>() : NullLogger<MvcMiddleware>.Instance;
            actionFinder = options.ActionFinder;
            viewEngine = options.ViewEngine;
        }

        public async Task InvokeAsync(IMiniAppContext context, ICallable next, CancellationToken cancellationToken = default)
        {
            try
            {
                var actionInfo = actionFinder.Find(context);
                if (actionInfo != null)
                {
                    // build a new local service collection, the new collection will contain services from app's collection and some request specific services
                    var localServiceCollection = new ServiceCollection();
                    foreach (var serv in serviceCollection)
                    {
                        localServiceCollection.Add(serv);
                    }
                    localServiceCollection.AddTransient(services => context);

                    var localServiceProvider = localServiceCollection.BuildServiceProvider();

                    if (ActivatorUtilities.CreateInstance(localServiceProvider, actionInfo.ControllerType) is Controller controller)
                    {
                        // init standard properties
                        controller.ControllerContext = new ControllerContext(context, viewEngine);

                        if (!await CallActionMethodAsync(localServiceProvider, controller, actionInfo, context, cancellationToken))
                        {
                            logger.LogError("Error processing action {a}", actionInfo.MethodInfo);
                            context.Response.StatusCode = HttpResponseCodes.InternalServerError;
                        }
                    }
                    else
                    {
                        logger.LogError("Error instantiating controller {c}", actionInfo.ControllerType);
                        context.Response.StatusCode = HttpResponseCodes.InternalServerError;

                        return;
                    } 
                }
                else
                {
                    await next.InvokeAsync(context, cancellationToken);
                }
            } catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error while processing request");
                context.Response.StatusCode = HttpResponseCodes.InternalServerError;

                return;
            }
        }

        private async Task<bool> CallActionMethodAsync(ServiceProvider localServiceProvider, Controller controller, ActionInfo actionInfo, IMiniAppContext context, CancellationToken cancellationToken)
        {
            /* how do we execute an action?
               - get action parameters
               - foreach parameter:
                  - find a service from localServiceProvider, if found, use it as an action parameter
                  - if not found, find a parameter by name from Request, if found, use it as an action parameter
                  - if not found, return an error (500 Internal Server Error) 
               - execute the action (synchronously or asynchronously)
               - if return value is not an IViewActionResult, call result.ToString() and return a ContentResult
               - otherwise, use ViewEngine to build content using the result as input, return data generated by ViewEngine               
            */

            var actionParameters = actionInfo.MethodInfo.GetParameters();
            var actionParameterValues = new List<object?>(actionParameters.Length);

            foreach (var parameter in actionParameters)
            {
                var parameterName = parameter.Name;
                if (string.IsNullOrEmpty(parameterName))
                {
                    logger.LogError("Parameter name cannot be empty");
                    return false;
                }

                var parameterType = parameter.ParameterType;
                if (parameterType == null)
                {
                    logger.LogError("Parameter type cannot be null");
                    return false;
                }

                var attributes = parameter.CustomAttributes;
                // check From* attributes
                if (!TryGetParameterSources(parameterName, attributes, out var parameterSources))
                {
                    return false;
                }

                var result = await TryCreateValueAsync(parameterName, parameterType, parameterSources, localServiceProvider, context, cancellationToken);
                if (result.IsCreated)
                {
                    actionParameterValues.Add(result.Value);
                }
                else
                {
                    logger.LogError("Cannot bind parameter {p}", parameterName);
                    return false;
                }
            }

            var actionResult = actionInfo.MethodInfo.Invoke(controller, actionParameterValues.ToArray());
            if (actionResult != null)
            {
                if (actionResult is Abstraction.IActionResult ar)
                {
                    var actionContext = new ActionResultContext(controller, actionInfo, context);

                    await ar.ExecuteResultAsync(actionContext);
                }
                else
                {
                    context.Response.Content = new MiniApp.Content.StringContent(actionResult.ToString() ?? string.Empty);
                }

                return true;
            }

            return false;
        }

        private bool TryGetParameterSources(string parameterName, IEnumerable<CustomAttributeData> attributes, out ParameterSources parameterSources)
        {
            parameterSources = ParameterSources.None;
            if (attributes != null)
            {
                foreach (var attribute in attributes)
                {
                    if (attribute.AttributeType == typeof(FromQueryAttribute))
                    {
                        if ((parameterSources & ParameterSources.Body) == ParameterSources.Body)
                        {
                            logger.LogError("Cannot bind parameter {p}: FromQuery can not be used with FromBody", parameterName);
                            return false;
                        }

                        parameterSources |= ParameterSources.Query;
                    }
                    else if (attribute.AttributeType == typeof(FromHeaderAttribute))
                    {
                        if ((parameterSources & ParameterSources.Body) == ParameterSources.Body)
                        {
                            logger.LogError("Cannot bind parameter {p}: FromHeader can not be used with FromBody", parameterName);
                            return false;
                        }

                        parameterSources |= ParameterSources.Header;
                    }
                    else if (attribute.AttributeType == typeof(FromFormAttribute))
                    {
                        if ((parameterSources & ParameterSources.Body) == ParameterSources.Body)
                        {
                            logger.LogError("Cannot bind parameter {p}: FromHeader can not be used with FromBody", parameterName);
                            return false;
                        }

                        parameterSources |= ParameterSources.Form;
                    }
                    else if (attribute.AttributeType == typeof(FromBodyAttribute))
                    {
                        if (parameterSources != ParameterSources.None) // FromBody can not be used with other sources
                        {
                            logger.LogError("Cannot bind parameter {p}: FromBody can not be used with other sources", parameterName);
                            return false;
                        }

                        parameterSources |= ParameterSources.Body;
                    }
                }
            }

            if (parameterSources == ParameterSources.None) // Can we get a parameter value from no sources? No!
            {
                parameterSources = ParameterSources.Any;
            }

            return true;
        }

        private async Task<CreateParameterValueResult> TryCreateValueAsync(string parameterName, Type parameterType, ParameterSources parameterSources, ServiceProvider localServiceProvider, IMiniAppContext context, CancellationToken cancellationToken)
        {
            /*
             * How to create an action parameter
             * ---------------------------------
             * Request parameter data type is string, so if action parameter is:
             * - A string: simply use it, else
             * - Try to find a TryParse method (public static bool TryParse(string, out <Action paramtere type?>), if found then use it, else
             * - Try to find a TypeConverter using TypeDescriptor.GetConverter, if found then use it, else
             * - Try to find a model binder (not implemented yet), else
             * - Return false, (then a bad request)
             */

            if (parameterSources == ParameterSources.Body)
            {
                var body = await context.Request.ReadAsStringAsync(cancellationToken);

                try
                {
                    var value = JsonSerializer.Deserialize(body, parameterType);

                    return CreateParameterValueResult.Success(value);
                } catch (Exception ex)
                {
                    logger.LogError(ex, "Error deserializing body to parameter: {p}", parameterName);

                    return CreateParameterValueResult.Fail();
                }
            }
            else
            {
                string? requestParameterValue = default;
                bool parameterFound = false;

                // if the parameter is defined with a specific location (Query, Header, Form), then we take it from there

                if ((parameterSources & ParameterSources.Query) == ParameterSources.Query)
                {
                    var requestParameter = context.Request.QueryParameters.Where(p => p.Key.Equals(parameterName, StringComparison.InvariantCultureIgnoreCase)).Select(p => p.Value).FirstOrDefault();
                    if (requestParameter != null) // a parameter found in Request.Query
                    {
                        requestParameterValue = requestParameter.Values.FirstOrDefault(); // TODO: we should support multi value parameters
                        parameterFound = true;
                    }
                }

                if (!parameterFound && (parameterSources & ParameterSources.Header) == ParameterSources.Header)
                {
                    if (context.Request.Headers.TryGetValue(parameterName, out var header)) // a parameter found in Request.Query
                    {
                        if (header != null)
                        {
                            requestParameterValue = header.Value.FirstOrDefault(); // TODO: we should support multi value parameters
                            parameterFound = true;
                        }
                    }
                }

                if (!parameterFound && (parameterSources & ParameterSources.Form) == ParameterSources.Form)
                {
                    var form = await context.Request.ReadFormAsync(cancellationToken: cancellationToken);

                    if (form != null)
                    {
                        if (form.TryGetValue(parameterName, out var values))
                        {
                            requestParameterValue = values.FirstOrDefault(); // TODO: we should support multi value parameters
                            parameterFound = true;
                        }
                    }
                }

                if (parameterFound)
                {
                    if (parameterType == typeof(string)) // no conversion required
                    {
                        return CreateParameterValueResult.Success(requestParameterValue);
                    }

                    if (requestParameterValue != null)
                    {
                        // looking for a public static bool TryParse method
                        var tryParseMethod = parameterType.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, null,
                            new Type[] { typeof(string), parameterType.MakeByRefType() },
                            null);
                        if (tryParseMethod != null && tryParseMethod.ReturnType == typeof(bool))
                        {
                            // normally, a TryParse method returns a bool value and accepts 2 parameters: a string and a parsed value
                            var tryParseParameters = new object?[] { requestParameterValue, null }; // null is place holder of the 2nd parameter (for example: out int? value in int.TryParse)
                            var b = (bool?)tryParseMethod.Invoke(null, tryParseParameters);

                            if (b.HasValue && b.Value)
                            {
                                return CreateParameterValueResult.Success(tryParseParameters[1]);
                            }
                        }
                        else
                        {
                            var converter = TypeDescriptor.GetConverter(parameterType);
                            if (converter != null) // if we can find a TypeConverter then we use it
                            {
                                if (converter.IsValid(requestParameterValue))
                                {
                                    return CreateParameterValueResult.Success(converter.ConvertFromString(requestParameterValue));
                                }
                                else
                                {
                                    return CreateParameterValueResult.Fail();
                                }
                            }

                            // todo: find a binder and convert data
                        }
                    }
                }
            }

            // if parameter not found, then we try to get a compatible value from DI
            var valueFromDI = localServiceProvider.GetService(parameterType);
            if (valueFromDI != null)
            {
                return CreateParameterValueResult.Success(valueFromDI);
            }

            var underlyingType = Nullable.GetUnderlyingType(parameterType);
            if (underlyingType != null) // this is a nullable type
            {
                return CreateParameterValueResult.Success(null);
            }

            return CreateParameterValueResult.Fail();
        }
    }
}