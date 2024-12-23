/*
 * opcfoundation.org:2024_10:starterkit:measurements
 *
 * A placeholder API that allows OpenAPI tools to be used to generate code for a companion specification.
 *
 * The version of the OpenAPI document: 1.00.0
 * Generated by: https://github.com/openapitools/openapi-generator.git
 */


using System;

namespace Measurements.Client
{
    /// <summary>
    /// A delegate to ExceptionFactory method
    /// </summary>
    /// <param name="methodName">Method name</param>
    /// <param name="response">Response</param>
    /// <returns>Exceptions</returns>
    public delegate Exception ExceptionFactory(string methodName, IApiResponse response);
}
