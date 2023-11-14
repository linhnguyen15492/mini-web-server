﻿using Microsoft.Extensions.Logging;
using MiniWebServer.Mvc.Abstraction;
using MiniWebServer.Mvc.Abstraction.ViewContent;
using MiniWebServer.Mvc.MiniRazorEngine;
using MiniWebServer.Mvc.MiniRazorEngine.Parser;

namespace MiniWebServer.Mvc.RazorEngine
{
    public class MiniRazorViewEngine : IViewEngine
    {
        public const string DefaultViewFolder = "Views";

        private readonly MiniRazorViewEngineOptions options;
        private readonly ILogger<MiniRazorViewEngine> logger;
        private readonly IViewFinder viewFinder;
        private readonly ITemplateParser templateParser;

        public MiniRazorViewEngine(MiniRazorViewEngineOptions options, ILogger<MiniRazorViewEngine> logger, ITemplateParser templateParser, IViewFinder? viewFinder = default)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.templateParser = templateParser ?? throw new ArgumentNullException(nameof(templateParser));
            this.logger = logger;
            this.viewFinder = viewFinder ?? new DefaultViewFinder(DefaultViewFolder);
        }

        public async Task<IViewContent?> RenderAsync(ActionResultContext context, string viewName, object? model, IDictionary<string, object> viewData)
        {
            // TODO: we should support caching compiled template, we will need to redesign a bit in this part

            try
            {
                /* 
                 How to render a view using razor?
                 - find a template string by view name
                 - compile the template to C# code
                 - compile the C# code to an assembly
                 - load the assembly and call it's InvokeAsync() function
                 - send back InvokeAsync result to client

                 What problems can we have?
                 - we need caching compiled C# files
                 - we need to design a parent class for compiled C# classes
                 - we will need to find a proper way to stream data generated by InvokeAsync back to client, taking everything and send at once back to 
                 client using a StringContent is not a good idea
                */

                var template = viewFinder.Find(context, viewName);
                if (template == null)
                {
                    logger.LogError("View not found: {v}", viewName);
                    return await Task.FromResult(new InternalErrorViewContent());
                }

                var parseResult = await templateParser.ParseAsync(viewName, template, model);
                if (parseResult.Compiled)
                {
                    string compiledContent = parseResult.CompiledContent;

                    return await Task.FromResult(new StringViewContent(compiledContent));
                }
                else
                {
                    logger.LogError(parseResult.Exception, "Error parsing view: {v}", viewName);
                    return await Task.FromResult(new InternalErrorViewContent(parseResult.Exception?.ToString() ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error rendering view");
                return await Task.FromResult(new InternalErrorViewContent(ex.Message + "\r\n" + ex.StackTrace));
            }
        }
    }
}