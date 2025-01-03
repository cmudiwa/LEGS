﻿using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ZimraEGS.ApiClient.Enums;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.ApiClient.Models;
using ZimraEGS.Helpers;
using ZimraEGS.Models;

namespace ZimraEGS.Controllers
{
    public class RelayController : Controller
    {

        private readonly ILogger<RelayController> _logger;

        public RelayController(ILogger<RelayController> logger)
        {
            _logger = logger;
        }

        [HttpPost("relay")]
        public async Task<IActionResult> ProcessFormData([FromForm] Dictionary<string, string> formData)
        {
            try
            {
                var relayData = new RelayData(formData);

                if (relayData.CertificateInfo == null)
                {
                    SetupViewModel setupViewModel = new SetupViewModel
                    {
                        Referrer = relayData.Referrer,
                        Api = relayData.Api,
                        Token = relayData.Token,
                        BusinessDetailsJson = relayData.BusinessDetailJson
                    };

                    TempData["SetupViewModel"] = JsonConvert.SerializeObject(setupViewModel);

                    return RedirectToAction("Register", "Setup");
                }

                _logger.LogInformation("{0} - {1} - {2}", relayData.Api, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown",relayData.CertificateInfo.IntegrationType);

                //if (relayData.EGSVersion <= 2410190001)
                //{

                //    var businessDetails = relayData.BusinessDetailJson;
                //    businessDetails = RelayDataHelper.ModifyStringCustomFields2(businessDetails, ManagerCustomField.AppVersionGuid, VersionHelper.GetVersion());

                //    var svm = new SetupViewModel
                //    {
                //        Referrer = relayData.Referrer,
                //        BusinessDetailsJson = businessDetails,
                //        Api = relayData.Api,
                //        Token = relayData.Token,
                //    };

                //    TempData["SetupViewModel"] = JsonConvert.SerializeObject(svm);
                //    return RedirectToAction("UpdateBusinessData", "Setup");
                //}

                if (relayData.BusinessReference.LastApprovalStatus == "REJECTED")
                {
                    if (relayData.FormKey != relayData.BusinessReference.ReceiptUUID)
                    {
                        ErrorViewModel errorViewModel = new ErrorViewModel
                        {
                            Referrer = relayData.Referrer,
                            ErrorMessage = "Last Invoice was Rejected, Cannot Reporting New Receipt"
                        };
                        return View("Error", errorViewModel);
                    }
                }

                var deviceID = relayData.CertificateInfo.DeviceID;
                var deviceModelName = relayData.CertificateInfo.DeviceModelName;
                var deviceModelVersion = relayData.CertificateInfo.DeviceModelVersion;

                ApiHelper apiHelper = new ApiHelper(relayData.CertificateInfo.Base64Pfx, relayData.CertificateInfo.IntegrationType);

                ServerResponse statusresponse = await apiHelper.SendGetRequestAsync(
                    "GetStatus",
                    deviceID,
                    deviceModelName,
                    deviceModelVersion
                );

                if (statusresponse.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    relayData.DeviceStatus = statusresponse.GetContentAs<GetStatusResponse>();

                    if (relayData.DeviceStatus.FiscalDayStatus == FiscalDayStatus.FiscalDayClosed)
                    {
                        // Check Certificate Valid
                        TimeSpan dateDifference = relayData.CertificateInfo.CertificateValidTill - TimeZoneHelper.GetSouthAfricaTime();
                        if (dateDifference.TotalDays < 30)
                        {
                            CertInfoViewModel certIfoVew = new CertInfoViewModel
                            {
                                BusinessDetailJson = relayData.BusinessDetailJson,
                                Api = relayData.Api,
                                Token = relayData.Token,
                                Referrer = relayData.Referrer,
                                CertificateInfo = relayData.CertificateInfo,
                            };

                            TempData["CertIfoVewModel"] = JsonConvert.SerializeObject(certIfoVew);
                            return RedirectToAction("IssueCertificate", "FiscalDevice");
                        }

                        OpenDayViewModel openDayViewModel = relayData.GetOpenDayViewModel();
                        TempData["OpenDayViewModel"] = JsonConvert.SerializeObject(openDayViewModel);
                        return RedirectToAction("OpenDay", "FiscalDevice");
                    }
                    else
                    {
                        // Check if invoice has been approved
                        if (relayData.ApprovalStatus == "APPROVED")
                        {
                            ApprovedInvoiceViewModel approvedInvoiceViewModel = relayData.GetApprovedInvoiceViewModel();
                            return View("ApprovedInvoice", approvedInvoiceViewModel);
                        }
                        else if (string.IsNullOrEmpty(relayData.ApprovalStatus) && relayData.BusinessReference.LastApprovalStatus == "REJECTED")
                        {
                            return RedirectToAction("Error", "Home");
                        }
                    }
                }
                else
                {
                    return StatusCode((int)statusresponse.StatusCode, new { Error = statusresponse.GetFullResponseAsJson() });
                }

                ServerResponse configresponse = await apiHelper.SendGetRequestAsync(
                    "GetConfig",
                    deviceID,
                    deviceModelName,
                    deviceModelVersion
                );

                if (Response.StatusCode == 200)
                {
                    relayData.DeviceConfig = configresponse.GetContentAs<GetConfigResponse>();
                }
                else
                {
                    return StatusCode((int)configresponse.StatusCode, new { Error = configresponse.GetFullResponseAsJson() });
                }

                ReceiptHelper rh = new ReceiptHelper(relayData);
                relayData.Receipt = rh.GenerateZimraReceipt();

                if (relayData.BusinessReference.LastApprovalStatus == "REJECTED")
                {
                    if (Convert.ToBase64String(relayData.Receipt.ReceiptDeviceSignature.Hash) != relayData.BusinessReference.LastReceiptHash)
                    {
                        ErrorViewModel errorViewModel = new ErrorViewModel
                        {
                            Referrer = relayData.Referrer,
                            ErrorMessage = "InvoiceHash does not Match Last Rejected Invoice"
                        };
                        return View("Error", errorViewModel);
                    }
                }

                RelayDataViewModel viewModel = relayData.GetRelayDataViewModel();

                return View(viewModel);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kesalahan saat memproses relay data");
                return StatusCode(500, new { Error = $"ProcessFormData > Internal server error: {ex.Message}" });
            }
        }

        public async Task<IActionResult> AjaxSubmitReceipt([FromForm] RelayDataViewModel model)
        {
            try
            {
                var deviceID = model.DeviceID;
                var deviceModelName = model.DeviceModelName;
                var deviceModelVersion = model.DeviceModelVersion;

                string ApprovalStatus = string.Empty;

                var payload = new SubmitReceiptRequest
                {
                    Receipt = JsonConvert.DeserializeObject<Receipt>(model.ReceiptJson)
                };

                ApiHelper apiHelper = new ApiHelper(model.Base64Pfx, model.IntegrationType);

                // Deserialize receipt JSON and make the API call
                ServerResponse response = await apiHelper.SendPostRequestAsync<SubmitReceiptRequest>(
                    "SubmitReceipt",
                    deviceID,
                    deviceModelName,
                    deviceModelVersion,
                    payload
                );

                //GetStatusResponse getStatusResponse;

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    SubmitReceiptResponse submitReceiptResponse = response.GetContentAs<SubmitReceiptResponse>();

                    ApprovalStatus = "APPROVED";

                    if (submitReceiptResponse.ValidationErrors != null)
                    {
                        var hasCriticalErrors = submitReceiptResponse.ValidationErrors.Any(error =>
                                    error.ValidationErrorColor.Equals("Grey", StringComparison.OrdinalIgnoreCase) ||
                                    error.ValidationErrorColor.Equals("Red", StringComparison.OrdinalIgnoreCase)
                                );

                        if (hasCriticalErrors)
                        {
                            ApprovalStatus = "REJECTED";
                        }
                    }

                    // Process Invoice JSON
                    var invoiceJson = model.InvoiceJson;

                    invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.ApprovalStatusGuid, ApprovalStatus);

                    invoiceJson = RelayDataHelper.UpdateOrCreateField(invoiceJson, model.ReceiptType == ReceiptType.FiscalInvoice ? "IssueDate" : "Date", model.ReceiptDate.ToString("yyyy-MM-dd"));
                    invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.ReceiptDateGuid, model.ReceiptDate.ToString("yyyy-MM-dd HH:mm:ss"));

                    invoiceJson = RelayDataHelper.UpdateOrCreateField(invoiceJson, "Reference", model.InvoiceNumber);

                    InvoiceReference invoiceReference = new InvoiceReference();
                    invoiceReference.ApprovalStatus = ApprovalStatus;
                    invoiceReference.ReceiptHash = model.ReceiptHash;
                    invoiceReference.ReceiptSignature = model.ReceiptSignature;
                    invoiceReference.SubmitReceiptResponse = submitReceiptResponse;

                    model.InvoiceReferenceJson = JsonConvert.SerializeObject(invoiceReference, Formatting.Indented);

                    invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.ServerResponseGuid, model.InvoiceReferenceJson);

                    invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.ReceiptQRCodeGuid, model.ReceiptQrCode);

                    if (!string.IsNullOrEmpty(ApprovalStatus))
                    {
                        invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.VerificationCodeGuid, model.TmpReceiptVerificationCode);
                    }

                    invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.DeviceSNGuid, model.DeviceSN);
                    invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.DeviceIDGuid, model.DeviceID);
                    invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.FiscalDayNoGuid, model.FiscalDayNo);
                    invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.ReceiptGlobalNoGuid, model.ReceiptGlobalNo);
                    invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.ReceiptCounterGuid, model.ReceiptCounter);

                    if (model.ReceiptType != ReceiptType.FiscalInvoice)
                    {
                        invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.DeviceIDRefGuid, model.DeviceIDRef);
                        invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.DeviceSNRefGuid, model.DeviceSNRef);
                        invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.FiscalDayNoRefGuid, model.FiscalDayNoRef);
                        invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.ReceiptGlobalNoRefGuid, model.ReceiptGlobalNoRef);
                        invoiceJson = RelayDataHelper.ModifyDecimalCustomFields2(invoiceJson, ManagerCustomField.ReceiptCounterRefGuid, model.ReceiptCounterRef);

                        invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.ReceiptNoRefGuid, model.ReceiptRefNo);
                        invoiceJson = RelayDataHelper.ModifyStringCustomFields2(invoiceJson, ManagerCustomField.ReceiptRefDateGuid, model.ReceiptRefDate.ToString("yyyy-MM-dd HH:mm:ss"));
                    }

                    // Process Business Details JSON

                    var businessJson = model.BusinessDetailJson;

                    BusinessReference businessreference = new BusinessReference();
                    if (!string.IsNullOrEmpty(model.BusinessReferenceJson))
                    {
                        businessreference = JsonConvert.DeserializeObject<BusinessReference>(model.BusinessReferenceJson);
                        if (businessreference == null) { businessreference = new BusinessReference(); }
                    }

                    businessreference.LastApprovalStatus = ApprovalStatus;
                    businessreference.LastReceiptCounter = model.ReceiptCounter;

                    if (ApprovalStatus == "APPROVED")
                    {
                        businessreference.LastReceiptGlobalNo = model.ReceiptGlobalNo;
                        businessreference.LastReceiptHash = model.ReceiptHash;
                        businessreference.ReceiptUUID = model.FormKey;

                        businessJson = RelayDataHelper.ModifyStringCustomFields2(businessJson, ManagerCustomField.BusinessReferenceGuid, JsonConvert.SerializeObject(businessreference, Formatting.Indented));

                        if (!string.IsNullOrEmpty(model.ReceiptJson))
                        {
                            var receipt = JsonConvert.DeserializeObject<Receipt>(model.ReceiptJson);

                            var newFiscalDaySummary = FiscalDayProcessor.GenerateFiscalDaySummary(receipt, model.FiscalDayNo);

                            if (!string.IsNullOrEmpty(model.FiscalDaySummaryJson?.Trim()) && model.FiscalDaySummaryJson.Trim() != "null")
                            {
                                var lastFiscalDaySummary = JsonConvert.DeserializeObject<FiscalDaySummary>(model.FiscalDaySummaryJson);
                                newFiscalDaySummary = FiscalDayProcessor.CombineFiscalDaySummaries(lastFiscalDaySummary, newFiscalDaySummary);
                            }

                            string fiscalDaySummaryJson = JsonConvert.SerializeObject(newFiscalDaySummary, Formatting.Indented);

                            // Modify the business JSON with the new fiscal day summary.
                            businessJson = RelayDataHelper.ModifyStringCustomFields2(businessJson, ManagerCustomField.FiscalDaySummaryGuid, fiscalDaySummaryJson);
                        }
                    }

                    // Create combined object
                    var combinedApiObject = new
                    {
                        ApiInvoice = new
                        {
                            ApiUrl = Utils.ConstructInvoiceApiUrl(model.Referrer, model.FormKey),
                            SecretKey = model.Token,
                            Payload = invoiceJson
                        },
                        ApiReference = new
                        {
                            ApiUrl = $"{model.Api}/business-details-form/38cf4712-6e95-4ce1-b53a-bff03edad273",
                            SecretKey = model.Token,
                            Payload = businessJson
                        },
                        SubmitReceiptResponse = submitReceiptResponse,
                        //GetStatusResponse = getStatusResponse
                    };

                    // Serialize combined object to JSON
                    string combinedJson = JsonConvert.SerializeObject(combinedApiObject, Formatting.Indented);

                    return Ok(combinedJson);
                }

                return StatusCode((int)response.StatusCode, response.GetContentAsString());
            }
            catch (Exception ex)
            {
                return StatusCode(500, Json(new { Error = ex.Message }));
            }
        }
    }
}
