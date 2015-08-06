﻿namespace ApiDocs.Validation
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using ApiDocs.Validation.Config;
    using ApiDocs.Validation.Error;
    using ApiDocs.Validation.Http;
    using ApiDocs.Validation.Json;
    using ApiDocs.Validation.Params;
    using Newtonsoft.Json;

    public class DocSet
    {
        #region Constants
        private const string DocumentationFileExtension = "*.md";
        #endregion

        #region Instance Variables
        readonly JsonResourceCollection resourceCollection = new JsonResourceCollection();
        #endregion

        #region Properties
        /// <summary>
        /// Location of the documentation set
        /// </summary>
        public string SourceFolderPath { get; private set; }

        /// <summary>
        /// Documentation files found in the source folder
        /// </summary>
        public DocFile[] Files { get; private set; }

        public ResourceDefinition[] Resources { get; private set; }
        public MethodDefinition[] Methods { get; private set; }

        public JsonResourceCollection ResourceCollection { get { return this.resourceCollection; } }
        
        public List<ScenarioDefinition> TestScenarios {get; internal set;}

        public IEnumerable<AuthScopeDefinition> AuthScopes
        {
            get
            {
                return this.ListFromFiles(x => x.AuthScopes);
            }
        }

        public IEnumerable<ErrorDefinition> ErrorCodes
        {
            get
            {
                return this.ListFromFiles(x => x.ErrorCodes);
            }
        }

        public ApiRequirements Requirements { get; internal set; }
        #endregion

        #region Constructors
        public DocSet(string sourceFolderPath)
        {
            sourceFolderPath = ResolvePathWithUserRoot(sourceFolderPath);
            if (sourceFolderPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                sourceFolderPath = sourceFolderPath.TrimEnd(Path.DirectorySeparatorChar);
            }

            this.SourceFolderPath = sourceFolderPath;
            this.ReadDocumentationHierarchy(sourceFolderPath);

            this.LoadRequirements();
            this.LoadTestScenarios();
        }

        public DocSet()
        {
            
        }
        #endregion

        private void LoadRequirements()
        {
            ApiRequirementsFile[] requirements = TryLoadConfigurationFiles<ApiRequirementsFile>(this.SourceFolderPath);
            var foundRequirements = requirements.FirstOrDefault();
            if (null != foundRequirements)
            {
                Console.WriteLine("Using API requirements file: {0}", foundRequirements.SourcePath);
                this.Requirements = foundRequirements.ApiRequirements;
            }
        }

        private void LoadTestScenarios()
        {
            this.TestScenarios = new List<ScenarioDefinition>();

            ScenarioFile[] files = TryLoadConfigurationFiles<ScenarioFile>(this.SourceFolderPath);
            foreach (var file in files)
            {
                Console.WriteLine("Found test scenario file: {0}", file.SourcePath);
                this.TestScenarios.AddRange(file.Scenarios);
            }
        }

        /// <summary>
        /// Looks for JSON files that are a member of the doc set and parses them
        /// to look for files of type T that can be loaded.
        /// </summary>
        /// <returns>The loaded configuration files.</returns>
        /// <typeparam name="T">The 1st type parameter.</typeparam>
        public static T[] TryLoadConfigurationFiles<T>(string path) where T : ConfigFile
        {
            List<T> validConfigurationFiles = new List<T>();

            DirectoryInfo docSetDir = new DirectoryInfo(path);
            var jsonFiles = docSetDir.GetFiles("*.json", SearchOption.AllDirectories);
            foreach (var file in jsonFiles)
            {
                using (var reader = file.OpenText())
                {
                    try {
                        var config = JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
                        if (null != config && config.IsValid)
                        {
                            validConfigurationFiles.Add(config);
                            config.SourcePath = file.FullName;
                        }
                    } 
                    catch (JsonSerializationException ex)
                    {
                        // TODO: Make it possible to log out an error here.
//                        RecordLogMessage(true, 
//                            "Configuration Load Error", 
//                            "Exception processing config file {0}: {1}", 
//                            file.FullName, 
//                            ex.Message);
                    }
                }
            }

            return validConfigurationFiles.ToArray();
        }

        public static string ResolvePathWithUserRoot(string path)
        {
            if (path.StartsWith(string.Concat("~", Path.DirectorySeparatorChar.ToString())))
            {
                var userFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(userFolderPath, path.Substring(2));
            }
            return path;
        }

        /// <summary>
        /// Scan all files in the documentation set to load
        /// information about resources and methods defined in those files
        /// </summary>
        public bool ScanDocumentation(out ValidationError[] errors)
        {
            var foundResources = new List<ResourceDefinition>();
            var foundMethods = new List<MethodDefinition>();

            var detectedErrors = new List<ValidationError>();

            this.resourceCollection.Clear();

            if (!this.Files.Any())
            {
                detectedErrors.Add(
                    new ValidationError(
                        ValidationErrorCode.NoDocumentsFound,
                        null,
                        "No markdown documentation was found in the current path."));
            }

            foreach (var file in this.Files)
            {
                ValidationError[] parseErrors;
                if (!file.Scan(out parseErrors))
                {
                    detectedErrors.AddRange(parseErrors);
                }

                foundResources.AddRange(file.Resources);
                foundMethods.AddRange(file.Requests);
            }
            this.resourceCollection.RegisterJsonResources(foundResources);

            this.Resources = foundResources.ToArray();
            this.Methods = foundMethods.ToArray();

            errors = detectedErrors.ToArray();
            return detectedErrors.Count == 0;
        }

        /// <summary>
        /// Validates that a particular HttpResponse matches the method definition and optionally the expected response.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="actualResponse"></param>
        /// <param name="expectedResponse"></param>
        /// <param name="errors"></param>
        /// <param name="silenceWarnings"></param>
        /// <param name="scenario"></param>
        /// <returns></returns>
        public bool ValidateApiMethod(MethodDefinition method, HttpResponse actualResponse, HttpResponse expectedResponse, out ValidationError[] errors, bool silenceWarnings, ScenarioDefinition scenario)
        {
            if (null == method) throw new ArgumentNullException("method");
            if (null == actualResponse) throw new ArgumentNullException("actualResponse");

            List<ValidationError> detectedErrors = new List<ValidationError>();

            // Verify the request is valid (headers, etc)
            method.VerifyHttpRequest(detectedErrors);

            // Verify that the expected response headers match the actual response headers
            ValidationError[] httpErrors;
            if (null != expectedResponse && !expectedResponse.CompareToResponse(actualResponse, out httpErrors))
            {
                detectedErrors.AddRange(httpErrors);
            }

            // Verify the actual response body is correct according to the schema defined for the response
            if (string.IsNullOrEmpty(actualResponse.Body) && (expectedResponse != null && !string.IsNullOrEmpty(expectedResponse.Body)))
            {
                detectedErrors.Add(new ValidationError(ValidationErrorCode.HttpBodyExpected, null, "Body missing from response (expected response includes a body or a response type was provided)."));
            }
            else if (!string.IsNullOrEmpty(actualResponse.Body))
            {
                ValidationError[] schemaErrors;
                if (method.ExpectedResponseMetadata == null || (string.IsNullOrEmpty(method.ExpectedResponseMetadata.ResourceType) && (expectedResponse != null && !string.IsNullOrEmpty(expectedResponse.Body))))
                {
                    detectedErrors.Add(new ValidationError(ValidationErrorCode.ResponseResourceTypeMissing, null, "Expected a response, but resource type on method is missing: {0}", method.Identifier));
                }
                else if (!this.resourceCollection.ValidateResponseMatchesSchema(method, actualResponse, expectedResponse, out schemaErrors))
                {
                    detectedErrors.AddRange(schemaErrors);
                }

                var responseValidation = actualResponse.IsResponseValid(method.SourceFile.DisplayName, method.SourceFile.Parent.Requirements);
                detectedErrors.AddRange(responseValidation.Messages);
            }

            // Verify any expectations in the scenario are met
            if (null != scenario)
            {
                scenario.ValidateExpectations(actualResponse, detectedErrors);
            }

            errors = detectedErrors.ToArray();

            if (silenceWarnings)
            {
                return !errors.Any(x => x.IsError);
            }
            return errors.Length == 0;
        }

        /// <summary>
        /// Scan through the document set looking for any broken links.
        /// </summary>
        /// <param name="includeWarnings"></param>
        /// <param name="errors"></param>
        /// <returns></returns>
        public bool ValidateLinks(bool includeWarnings, out ValidationError[] errors)
        {
            List<ValidationError> foundErrors = new List<ValidationError>();

            Dictionary<string, bool> orphanedPageIndex = this.Files.ToDictionary(x => this.RelativePathToFile(x, true), x => true);

            foreach (var file in this.Files)
            {
                ValidationError[] localErrors;
                string [] linkedPages;
                if (!file.ValidateNoBrokenLinks(includeWarnings, out localErrors, out linkedPages))
                {
                    foundErrors.AddRange(localErrors);
                }
                foreach (string pageName in linkedPages)
                {
                    orphanedPageIndex[pageName] = false;
                }

                if (null != file.Annotation && file.Annotation.TocPath != null)
                {
                    var pageName = this.CleanUpDisplayName(file.DisplayName);
                    orphanedPageIndex[pageName] = false;
                }
            }

            foundErrors.AddRange(from o in orphanedPageIndex
                                 where o.Value
                                 select new ValidationWarning(ValidationErrorCode.OrphanedDocumentPage, null, "Page {0} has no incoming links.", o.Key));

            errors = foundErrors.ToArray();
            return !errors.WereWarningsOrErrors();
        }

        /// <summary>
        /// Remove the initial path separator and make sure all of the remaining ones are forward-slash
        /// </summary>
        /// <param name="displayName"></param>
        /// <returns></returns>
        private string CleanUpDisplayName(string displayName)
        {
            if (displayName.StartsWith(Path.DirectorySeparatorChar.ToString()))
                displayName = displayName.Substring(1);
            return displayName.Replace(Path.DirectorySeparatorChar, '/');
        }


        /// <summary>
        /// Pull the file and folder information from SourceFolderPath into the object
        /// </summary>
        private void ReadDocumentationHierarchy(string path)
        {
            DirectoryInfo sourceFolder = new DirectoryInfo(path);
            if (!sourceFolder.Exists)
            {
                throw new FileNotFoundException(string.Format("Cannot find documentation. Directory doesn't exist: {0}", sourceFolder.FullName));
            }

            var fileInfos = sourceFolder.GetFiles(DocumentationFileExtension, SearchOption.AllDirectories);

            var relativeFilePaths = from fi in fileInfos
                                    select new DocFile(this.SourceFolderPath, this.RelativePathToFile(fi.FullName), this);
            this.Files = relativeFilePaths.ToArray();
        }

        internal string RelativePathToFile(DocFile file, bool urlStyle = false)
        {
            return RelativePathToFile(file.FullPath, this.SourceFolderPath, urlStyle);
        }

        /// <summary>
        /// Generate a relative file path from the base path of the documentation
        /// </summary>
        /// <param name="fileFullName"></param>
        /// <param name="urlStyle"></param>
        /// <returns></returns>
        internal string RelativePathToFile(string fileFullName, bool urlStyle = false)
        {
            return RelativePathToFile(fileFullName, this.SourceFolderPath, urlStyle);
        }

        internal static string RelativePathToFile(string fileFullName, string rootFolderPath, bool urlStyle = false)
        {
            Debug.Assert(fileFullName.StartsWith(rootFolderPath), "fileFullName doesn't start with the source folder path");

            var path = fileFullName.Substring(rootFolderPath.Length);
            if (urlStyle)
            {
                // Should look like this "items/foobar.md" instead of @"\items\foobar.md"
                string[] components = path.Split(Path.DirectorySeparatorChar);
                path = components.ComponentsJoinedByString("/");
            }
            return path;
        }

        /// <summary>
        /// Generates a relative path from deepFilePath to shallowFilePath.
        /// </summary>
        /// <param name="shallowFilePath"></param>
        /// <param name="urlStyle"></param>
        /// <param name="deepFilePath"></param>
        /// <returns></returns>
        public static string RelativePathToRootFromFile(string deepFilePath, string shallowFilePath, bool urlStyle = false)
        {
            if (deepFilePath == null) throw new ArgumentNullException("deepFilePath");
            if (shallowFilePath == null) throw new ArgumentNullException("shallowFilePath");

            // example:
            // deep file path "/auth/auth_msa.md"
            // shallow file path "template/stylesheet.css"
            // returns "../template/stylesheet.css"

            if (deepFilePath.Equals(shallowFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return urlStyle ? "#" : Path.GetFileName(shallowFilePath);
            }

            List<string> deepPathComponents = new List<string>(deepFilePath.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
            List<string> shallowPathComponents = new List<string>(shallowFilePath.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));

            while(deepPathComponents.Count > 0 && shallowPathComponents.Count > 0 && deepPathComponents[0].Equals(shallowPathComponents[0], StringComparison.OrdinalIgnoreCase))
            {
                deepPathComponents.RemoveAt(0);
                shallowPathComponents.RemoveAt(0);
            }
            
            int depth = Math.Max(0, deepPathComponents.Count - 1);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < depth; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(urlStyle ? "/" : "\\");
                }
                sb.Append("..");
            }

            if (sb.Length > 0)
            {
                sb.Append(urlStyle ? "/" : "\\");
            }
            sb.Append(shallowPathComponents.ComponentsJoinedByString(urlStyle ? "/" : "\\"));

            if (sb.Length == 0)
                Console.WriteLine("Failed to resolve link for {0}", shallowFilePath);

            return sb.ToString();
        }

        private IEnumerable<T> ListFromFiles<T>(Func<DocFile, IEnumerable<T>> perFileSource)
        {
            List<T> collected = new List<T>();
            foreach (var file in this.Files)
            {
                var data = perFileSource(file);
                if (null != data)
                    collected.AddRange(data);
            }
            return collected;
        }


        internal DocFile LookupFileForPath(string path)
        {
            // Translate path into what we're looking for
            string[] pathComponents = path.Split('/', '\\');
            string pathSeperator = Path.DirectorySeparatorChar.ToString();
            string displayName = pathSeperator + pathComponents.ComponentsJoinedByString(pathSeperator);

            var query = from f in this.Files
                        where f.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase)
                        select f;

            return query.FirstOrDefault();
        }
        
        



    }

    public class DocSetEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public bool Verbose { get; private set; }
        public string Title {get; private set;}

        public DocSetEventArgs(string message, bool verbose = false)
        {
            this.Message = message;
            this.Verbose = verbose;
        }

        public DocSetEventArgs(bool verbose, string title, string format, params object[] parameters)
        {
            this.Title = title;
            this.Message = string.Format(format, parameters);
            this.Verbose = verbose;
        }
    }
}