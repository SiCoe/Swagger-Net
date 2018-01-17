﻿using Newtonsoft.Json;
using Swagger.Net.Swagger.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Web.Http.Description;
using System.Reflection;
using Swagger.Net.Swagger.Annotations;

namespace Swagger.Net
{
    public class SwaggerGenerator : ISwaggerProvider
    {
        private readonly IApiExplorer _apiExplorer;
        private readonly JsonSerializerSettings _jsonSerializerSettings;
        private readonly IDictionary<string, Info> _apiVersions;
        private readonly SwaggerGeneratorOptions _options;

        public SwaggerGenerator(
            IApiExplorer apiExplorer,
            JsonSerializerSettings jsonSerializerSettings,
            IDictionary<string, Info> apiVersions,
            SwaggerGeneratorOptions options = null)
        {
            _apiExplorer = apiExplorer;
            _jsonSerializerSettings = jsonSerializerSettings;
            _apiVersions = apiVersions;
            _options = options ?? new SwaggerGeneratorOptions();
        }

        public SwaggerDocument GetSwagger(string rootUrl, string apiVersion)
        {
            var schemaRegistry = new SchemaRegistry(_jsonSerializerSettings, _options);

            Info info;
            _apiVersions.TryGetValue(apiVersion, out info);
            if (info == null)
                throw new UnknownApiVersion(apiVersion);

            HashSet<string> operationNames = new HashSet<string>();
            var apiDescriptions = GetApiDescriptionsFor(apiVersion)
                .Where(apiDesc => !(_options.IgnoreObsoleteActions && apiDesc.IsObsolete()))
                .ToList();

            var paths = apiDescriptions
                .OrderBy(_options.GroupingKeySelector, _options.GroupingKeyComparer)
                .GroupBy(apiDesc => apiDesc.RelativePathSansQueryString())
                .ToDictionary(group => "/" + group.Key, group => CreatePathItem(group, schemaRegistry, operationNames));

            var tags = apiDescriptions
                .OrderBy(_options.GroupingKeySelector, _options.GroupingKeyComparer)
                .Select(a => new Tag {description = a.Documentation, name = _options.GroupingKeySelector(a) })
                .Distinct(new TagNameEqualityComparer())
                .ToList();

            var controllers = apiDescriptions
                .GroupBy(x => x.ActionDescriptor.ControllerDescriptor)
                .Select(x => new
                {
                    name = x.Key.ControllerName,
                    context = new ModelFilterContext(x.Key.ControllerType, null, null)
                });

            foreach (var filter in _options.ModelFilters)
            {
                foreach (var c in controllers)
                {
                    var model = new Schema();
                    filter.Apply(model, c.context);
                    if (!string.IsNullOrEmpty(model.description))
                        tags.Where(t => t.name.Equals(c.name)).First().description = model.description;
                }
            }

            var rootUri = new Uri(rootUrl);
            var port = (!rootUri.IsDefaultPort) ? ":" + rootUri.Port : string.Empty;

            var swaggerDoc = new SwaggerDocument
            {
                info = info,
                host = rootUri.Host + port,
                basePath = (rootUri.AbsolutePath != "/") ? rootUri.AbsolutePath : null,
                tags = tags,
                schemes = (_options.Schemes != null) ? _options.Schemes.ToList() : new[] { rootUri.Scheme }.ToList(),
                paths = paths,
                definitions = schemaRegistry.Definitions,
                securityDefinitions = _options.SecurityDefinitions
            };

            foreach (var filter in _options.DocumentFilters)
            {
                filter.Apply(swaggerDoc, schemaRegistry, _apiExplorer);
            }

            return swaggerDoc;
        }

        private IEnumerable<ApiDescription> GetApiDescriptionsFor(string apiVersion)
        {
            return (_options.VersionSupportResolver == null)
                ? _apiExplorer.ApiDescriptions
                : _apiExplorer.ApiDescriptions.Where(apiDesc => _options.VersionSupportResolver(apiDesc, apiVersion));
        }

        private PathItem CreatePathItem(IEnumerable<ApiDescription> apiDescriptions, SchemaRegistry schemaRegistry, HashSet<string> operationNames)
        {
            var pathItem = new PathItem();

            // Group further by http method
            var perMethodGrouping = apiDescriptions
                .GroupBy(apiDesc => apiDesc.HttpMethod.Method.ToLower());

            foreach (var group in perMethodGrouping)
            {
                var httpMethod = group.Key;

                var apiDescription = (group.Count() == 1)
                    ? group.First()
                    : _options.ConflictingActionsResolver(group);

                switch (httpMethod)
                {
                    case "get":
                        pathItem.get = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                    case "put":
                        pathItem.put = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                    case "post":
                        pathItem.post = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                    case "delete":
                        pathItem.delete = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                    case "options":
                        pathItem.options = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                    case "head":
                        pathItem.head = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                    case "patch":
                        pathItem.patch = CreateOperation(apiDescription, schemaRegistry, operationNames);
                        break;
                }
            }

            return pathItem;
        }

        private Operation CreateOperation(ApiDescription apiDesc, SchemaRegistry schemaRegistry, HashSet<string> operationNames)
        {
            var parameters = apiDesc.ParameterDescriptions
                .Select(paramDesc =>
                {
                    string location = GetParameterLocation(apiDesc, paramDesc);
                    return CreateParameter(location, paramDesc, schemaRegistry);
                })
                 .ToList();

            var description = apiDesc.ActionDescriptor.GetCustomAttributes<SwaggerDescriptionAttribute>()
                .FirstOrDefault();

            var responses = new Dictionary<string, Response>();
            var responseType = apiDesc.ResponseType();
            if (responseType == null || responseType == typeof(void))
                responses.Add("204", new Response { description = "No Content" });
            else
                responses.Add("200", new Response { description = "OK", schema = schemaRegistry.GetOrRegister(responseType) });

            var operation = new Operation
            {
                tags = new[] { _options.GroupingKeySelector(apiDesc) },
                operationId = this.GetUniqueFriendlyId(apiDesc, operationNames),
                description = description?.Description,
                summary = description?.Summary,
                produces = apiDesc.Produces().ToList(),
                consumes = apiDesc.Consumes().ToList(),
                parameters = parameters.Any() ? parameters : null, // parameters can be null but not empty
                responses = responses,
                deprecated = apiDesc.IsObsolete() ? true : (bool?)null
            };

            foreach (var filter in _options.OperationFilters)
            {
                filter.Apply(operation, schemaRegistry, apiDesc);
            }

            return operation;
        }

        public string GetUniqueFriendlyId(ApiDescription apiDesc, HashSet<string> operationNames)
        {
            string friendlyId = apiDesc.FriendlyId();

            if (operationNames.Contains(friendlyId))
            {
                friendlyId = apiDesc.FriendlyId2();
            }

            int nextFriendlyIdPostfix = 1;
            while (operationNames.Contains(friendlyId))
            {
                friendlyId = string.Format("{0}_{1}", apiDesc.FriendlyId2(), nextFriendlyIdPostfix);
                nextFriendlyIdPostfix++;
            }
            operationNames.Add(friendlyId);
            return friendlyId;
        }

        private string GetParameterLocation(ApiDescription apiDesc, ApiParameterDescription paramDesc)
        {
            if (apiDesc.RelativePathSansQueryString().Contains("{" + paramDesc.Name + "}"))
                return "path";
            else if (paramDesc.Source == ApiParameterSource.FromBody && apiDesc.HttpMethod != HttpMethod.Get)
                return "body";
            else
                return "query";
        }

        private Parameter CreateParameter(string location, ApiParameterDescription paramDesc, SchemaRegistry schemaRegistry)
        {
            var parameter = new Parameter
            {
                @in = location,
                name = paramDesc.Name,
            };

            if (paramDesc.ParameterDescriptor == null)
            {
                parameter.type = "string";
                parameter.required = true;
            }
            else
            {
                parameter.pattern = paramDesc.GetRegularExpressionAttribute()?.Pattern;
                parameter.required = location == "path" || !paramDesc.ParameterDescriptor.IsOptional;
                parameter.description = paramDesc.Documentation;
                if (parameter.description == null)
                    parameter.description = paramDesc.GetDescriptionAttribute()?.Description;

                var schema = schemaRegistry.GetOrRegister(paramDesc.ParameterDescriptor.ParameterType);
                if (parameter.@in == "body")
                    parameter.schema = schema;
                else
                    parameter.PopulateFrom(schema);

                if (paramDesc.ParameterDescriptor.DefaultValue != null)
                    parameter.@default = paramDesc.ParameterDescriptor.DefaultValue;
            }
            return parameter;
        }
    }
}
