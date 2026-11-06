namespace SkylightPhotoSync.Sources.Amazon;

/// <summary>
/// One successful response as seen by <see cref="AmazonPhotosSource.ResponseObserver"/>, so the
/// fixture recorder can capture raw bodies. <see cref="Body"/> is empty for content downloads.
/// </summary>
/// <param name="Kind">Which endpoint answered; one of the <c>AmazonEndpoints.Kind*</c> constants.</param>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="ContentType">The response's <c>Content-Type</c>, when sent.</param>
/// <param name="ContentLength">The response's <c>Content-Length</c>, when sent.</param>
/// <param name="Body">The JSON body, or empty for binary content.</param>
public sealed record AmazonResponse(string Kind, int StatusCode, string? ContentType, long? ContentLength, string Body);
