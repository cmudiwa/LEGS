﻿using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.IO.Compression;
using System.Net;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Helpers;
using ZimraEGS.Models;


namespace ZimraEGS.Controllers
{
    public partial class SetupController : Controller
    {

        private readonly ILogger<SetupController> _logger;

        public SetupController(ILogger<SetupController> logger)
        {
            _logger = logger;
        }

        [HttpPost("Update")]
        public IActionResult UpdateCustomField([FromForm] Dictionary<string, string> formData)
        {
            try
            {
                var Referrer = Utils.GetValue(formData, "Referrer");
                var FormKey = Utils.GetValue(formData, "Key");
                var Data = Utils.GetValue(formData, "Data");
                var Api = Utils.GetValue(formData, "Api");
                var Token = Utils.GetValue(formData, "Token");

                _logger.LogInformation("Setup API Diakses oleh : {0} IP address: {1}", Api, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown");

                var BusinessDetailJson = RelayDataHelper.GetValueJson(Data, "BusinessDetails");

                if (!string.IsNullOrEmpty(FormKey))
                {
                    SetupViewModel setupViewModel = new SetupViewModel
                    {
                        Referrer = Referrer,
                        Api = Api,
                        Token = Token,
                        BusinessDetailsJson = BusinessDetailJson
                    };

                    return View("UpdateBusinessData", setupViewModel);
                }

                return View("index", "Home");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kesalahan saat memproses setup api");
                throw new Exception(ex.Message);
            }
        }

        [HttpGet("Setup/UpdateBusinessData")]
        public IActionResult UpdateBusinessData()
        {
            var svmJson = TempData["SetupViewModel"] as string;
            if (string.IsNullOrEmpty(svmJson))
            {
                return Content("No TempData found.");
            }

            var viewModel = JsonConvert.DeserializeObject<SetupViewModel>(svmJson);
            return View(viewModel);
        }

        public IActionResult Register()
        {
            SetupViewModel model = null;

            if (TempData["SetupViewModel"] != null)
            {
                model = JsonConvert.DeserializeObject<SetupViewModel>(TempData["SetupViewModel"].ToString());
            }

            if (model == null)
            {
                return RedirectToAction("Index");
            }
            return View("Index", model); // This will return the Index view with the model
        }


        [HttpGet("setup/GetCfData")]
        public IActionResult CustomFieldJson()
        {
            try
            {
                string jsonData = Utils.ReadEmbeddedResource("cfData.json");
                return Content(jsonData, "application/json");
            }
            catch (Exception ex)
            {
                return BadRequest($"Failed to load resource: {ex.Message}");
            }
        }

        [HttpPost("setup/verifytaxpayer")]
        public async Task<IActionResult> VerifyTaxPayerInformation([FromBody] VerifyTaxPayerDto verifyTaxPayerDto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new { Errors = "Invalid input data" });
            }

            try
            {
                VerifyTaxpayerInformationRequest verifyTaxpayerInformationRequest = new VerifyTaxpayerInformationRequest
                {
                    ActivationKey = verifyTaxPayerDto.ActivationKey,
                    DeviceSerialNo = verifyTaxPayerDto.DeviceSerialNumber
                };

                PlatformType integrationType = (PlatformType)Enum.Parse(typeof(PlatformType), verifyTaxPayerDto.IntegrationType);
                ApiHelper apiHelper = new ApiHelper(integrationType);
                ServerResponse serverResponse = await apiHelper.VerifyTaxpayerInformationAsync(
                    verifyTaxPayerDto.DeviceID,
                    verifyTaxpayerInformationRequest
                );

                if (serverResponse.StatusCode == HttpStatusCode.OK)
                {
                    return Ok(serverResponse.ResponseContent);
                }

                return BadRequest(new { Errors = serverResponse.GetContentAsString() });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = $"Internal server error: {ex.Message}" });
            }
        }

        [HttpPost("setup/registerdevice")]
        public async Task<IActionResult> RegisterDeviceAsync([FromBody] DeviceRegistrationDto deviceRegistration)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                return BadRequest(new { Errors = errors });
            }

            if (deviceRegistration == null || string.IsNullOrEmpty(deviceRegistration.CommonName))
            {
                return BadRequest(new { Errors = "Common name is required." });
            }

            try
            {
                string errMessage = string.Empty;

                var (generatedCsr, privateKey) = RSA_CryptoHelper.GenerateCSR(deviceRegistration.CommonName);

                // Register Device
                RegisterDeviceRequest registerDeviceRequest = new RegisterDeviceRequest
                {
                    ActivationKey = deviceRegistration.ActivationKey,
                    CertificateRequest = generatedCsr
                };

                PlatformType integrationType = (PlatformType)Enum.Parse(typeof(PlatformType), deviceRegistration.IntegrationType);

                ApiHelper apiHelper = new ApiHelper(PlatformType.Simulation);
                ServerResponse serverResponse = await apiHelper.RegisterDeviceAsync(deviceRegistration.DeviceID, deviceRegistration.DeviceModelName, deviceRegistration.DeviceModelVersion, registerDeviceRequest);

                if (serverResponse.StatusCode == HttpStatusCode.OK)
                {
                    var deviceCertificate = serverResponse.GetContentAs<RegisterDeviceResponse>().Certificate;

                    string pfxbyte = Utilities.GeneratePfx(Utilities.CleanBase64String(privateKey), Utilities.CleanBase64String(deviceCertificate), (string)null);

                    var response = new
                    {
                        GeneratedCsr = Utilities.CleanBase64String(generatedCsr),
                        PrivateKey = Utilities.CleanBase64String(privateKey),
                        DeviceCertificate = Utilities.CleanBase64String(deviceCertificate),
                        Base64Pfx = pfxbyte
                    };

                    return Ok(response);
                }
                else
                {
                    errMessage = serverResponse.GetFullResponseAsJson();
                }

                return BadRequest(new { Errors = errMessage });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Errors = "Error generating CSR: " + ex.Message });
            }
        }

        [HttpPost("setup/getconfig")]
        public async Task<IActionResult> GetDeviceConfig([FromBody] GetConfigDto getConfigDTO)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                return BadRequest(new { Errors = errors });
            }

            if (getConfigDTO == null || string.IsNullOrEmpty(getConfigDTO.Base64Pfx))
            {
                return BadRequest(new { Errors = "Base64 PFX is required." });
            }

            try
            {
                string errMessage = string.Empty;

                PlatformType integrationType = (PlatformType)Enum.Parse(typeof(PlatformType), getConfigDTO.IntegrationType);

                ApiHelper apiHelper = new ApiHelper(getConfigDTO.Base64Pfx, PlatformType.Simulation);

                ServerResponse serverResponse = await apiHelper.GetConfigAsync(getConfigDTO.DeviceID, getConfigDTO.DeviceModelName, getConfigDTO.DeviceModelVersion);

                if (serverResponse.StatusCode == HttpStatusCode.OK)
                {
                    GetConfigResponse getConfigResponse = serverResponse.GetContentAs<GetConfigResponse>();

                    var response = new
                    {
                        DeviceOperatingMode = getConfigResponse.DeviceOperatingMode,
                        ApplicableTaxes = string.Join("#", getConfigResponse.ApplicableTaxes.Select(x => $"{x.TaxID} - {x.TaxName} - {x.TaxPercent}")),
                        TaxPayerDayMaxHrs = getConfigResponse.TaxPayerDayMaxHrs,
                        TaxpayerDayEndNotificationHrs = getConfigResponse.TaxpayerDayEndNotificationHrs,
                        CertificateValidTill = getConfigResponse.CertificateValidTill,
                        QrUrl = getConfigResponse.QrUrl
                    };

                    return Ok(response);
                }
                else
                {
                    errMessage = serverResponse.GetContentAsString();
                }

                Console.WriteLine(errMessage);

                return BadRequest(new { Errors = errMessage });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Errors = "Error Get Device Configuration: " + ex.Message });
            }
        }


        [HttpPost("setup/savecertificate")]
        public IActionResult SaveCertificate(SetupViewModel viewModel)
        {
            try
            {
                CertificateInfo certificateInfo = viewModel.CertificateInfo;

                var base64CertificateInfo = ObjectCompressor.SerializeToBase64String(certificateInfo);

                var businessDetails = viewModel.BusinessDetails;
                if (string.IsNullOrEmpty(viewModel.BusinessDetails))
                {
                    businessDetails = @"
                    {
                        ""Name"": """",
                        ""Address"": """",
                        ""CustomFields2"": {
                            ""Decimals"": {
                            },
                            ""Strings"": {
                                ""f7214db4-6726-4aa9-b5cd-3ff90ce9ba6c"": """",
                                ""0f0bf167-4b63-493d-ab45-049a76a07f46"": """",
                                ""6a347c55-735a-4a38-8cf6-0db93dce2ded"": """",
                                ""329de867-9cf1-4dfe-8b06-5084bce788c8"": """"
                            }
                        },
                    }";
                }

                businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Name", certificateInfo.TaxPayerName);
                businessDetails = RelayDataHelper.UpdateOrCreateField(businessDetails, "Address", certificateInfo.ToBusinessAddress());
                businessDetails = RelayDataHelper.ModifyStringCustomFields2(businessDetails, ManagerCustomField.CertificateInfoGuid, base64CertificateInfo);
                businessDetails = RelayDataHelper.ModifyStringCustomFields2(businessDetails, ManagerCustomField.AppVersionGuid, VersionHelper.GetVersion());

                viewModel.BusinessDetails = businessDetails;

                viewModel.BusinessDetailsJson = JsonConvert.SerializeObject(businessDetails);

                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        var infoEntry = archive.CreateEntry($"{certificateInfo.VatNumber}_{certificateInfo.DeviceID.ToString().PadLeft(10, '0')}_{certificateInfo.IntegrationType.ToString()}.json");

                        using (var writer = new StreamWriter(infoEntry.Open()))
                        {
                            var infoJson = JsonConvert.SerializeObject(certificateInfo, Newtonsoft.Json.Formatting.Indented);
                            writer.Write(infoJson);
                        }
                    }

                    memoryStream.Position = 0;

                    var fileContent = memoryStream.ToArray();
                    viewModel.FileContent = Convert.ToBase64String(fileContent);
                    viewModel.Filename = $"{certificateInfo.VatNumber}_{certificateInfo.DeviceID.ToString().PadLeft(10, '0')}_{certificateInfo.IntegrationType.ToString()}.zip";
                    viewModel.IsFileReady = true;
                }
            }
            catch
            {
                throw;
            }
            return View("index", viewModel);
        }
    }
}