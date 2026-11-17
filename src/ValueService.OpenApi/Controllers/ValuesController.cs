using Microsoft.AspNetCore.Mvc;

namespace ValueService.OpenApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ValuesController(ILogger<ValuesController> logger) : ControllerBase
{
  [HttpGet("badcode")]
  public string BadCode() => throw new ArgumentException("Some bad code was executed!");

  [HttpGet]
  [ProducesResponseType<string>(StatusCodes.Status200OK)]
  public IActionResult Get()
  {
    string ocelotRequestId = Request.Headers["OcelotRequestId"].ToString() is { Length: > 0 } requestId
      ? requestId
      : "N/A";

    string msg =
      $"Url: {Request.Scheme}://{Request.Host}, Method: {Request.Method}, Path: {Request.Path}, OcelotRequestId: {ocelotRequestId}";
    logger.LogInformation("{Message}", msg);
    return Ok(msg);
  }

  [HttpGet("status")]
  [ProducesResponseType<string>(StatusCodes.Status200OK)]
  public IActionResult Status()
  {
    string msg = $"Running on {Request.Host}";
    logger.LogInformation("{Message}", msg);
    return Ok(msg);
  }
}
