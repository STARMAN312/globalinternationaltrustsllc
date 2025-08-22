﻿using globalinternationaltrusts.Data;
using globalinternationaltrusts.Models;
using globalinternationaltrusts.Services;
using Hangfire;
using Mailjet.Client.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NuGet.Common;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace globalinternationaltrusts.Controllers
{
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<HomeController> _logger;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly MarketDataService _marketDataService;
        private readonly IConfiguration _configuration;
        private readonly string _PaypalClientId; 
        private readonly string _PaypalSecret; 
        private readonly string _PaypalUrl;
        private readonly MailJetService _mailJetService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWebHostEnvironment _env;
        private readonly string _frontendBaseUrl;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext context, 
            ILogger<HomeController> logger, 
            UserManager<ApplicationUser> userManager, 
            RoleManager<IdentityRole> roleManager, 
            MarketDataService marketDataService, 
            IConfiguration configuration, 
            MailJetService mailJetService, 
            IHttpClientFactory httpClientFactory,
            IWebHostEnvironment env,
            IOptions<AppSettings> options
            )
        {
            _context = context;
            _logger = logger;
            _userManager = userManager;
            _roleManager = roleManager;
            _signInManager = signInManager;
            _marketDataService = marketDataService;
            _configuration = configuration;
            _PaypalClientId = _configuration["PayPalSettings:ClientId"]!;
            _PaypalSecret = _configuration["PayPalSettings:Secret"]!;
            _PaypalUrl = _configuration["PayPalSettings:Url"]!;
            _mailJetService = mailJetService;
            _httpClientFactory = httpClientFactory;
            _env = env;
            _frontendBaseUrl = options.Value.FrontendBaseUrl;
        }

        [Authorize(Roles = "Client")]
        private async Task<string> GetPaypalAccessToken()
        {
            string accessToken = "";
            string url = _PaypalUrl + "/v1/oauth2/token"; // Ensure this is fully qualified

            using (var client = _httpClientFactory.CreateClient())
            {
                string credentials64 = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(_PaypalClientId + ":" + _PaypalSecret));

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials64);

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(
                        "grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded")
                };

                var httpResponse = await client.SendAsync(requestMessage);

                if (httpResponse.IsSuccessStatusCode)
                {
                    var strResponse = await httpResponse.Content.ReadAsStringAsync();
                    var jsonResponse = JsonNode.Parse(strResponse);

                    if (jsonResponse != null)
                    {
                        accessToken = jsonResponse["access_token"]?.ToString() ?? "";
                    }
                }
                else
                {
                    var error = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to get PayPal access token: {error}");
                }
            }

            return accessToken;
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> Deposit()
        {

            ViewBag.HideBanner = true;

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            if (currentUser != null && currentUser.IsBanned)
            {
                return RedirectToAction("Index");
            }

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts!)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            if (user == null)
                return RedirectToAction("Login");

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }

            DepositVM depositView = new DepositVM
            {
                BankAccounts = user.BankAccounts,
            };

            ViewBag.PaypalClientId = _PaypalClientId;
            return View(depositView);
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        public async Task<IActionResult> ProcessDeposit([FromBody] JsonObject Data)
        {
            var totalAmount = Data?["amount"]?.ToString();
            var currency = Data?["currency"]?.ToString() ?? "USD"; // Default to USD
            if (totalAmount == null)
            {
                return new JsonResult(new { Id = "" });
            }

            JsonObject createOrderRequest = new JsonObject
            {
                ["intent"] = "CAPTURE"
            };

            JsonObject amount = new JsonObject
            {
                ["currency_code"] = "USD",
                ["value"] = totalAmount
            };

            JsonObject purchaseUnit = new JsonObject
            {
                ["amount"] = amount
            };

            JsonArray purchaseUnits = new JsonArray
            {
                purchaseUnit
            };

            createOrderRequest.Add("purchase_units", purchaseUnits);

            string accessToken = await GetPaypalAccessToken();
            string url = _PaypalUrl + "/v2/checkout/orders";

            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(createOrderRequest.ToString(), Encoding.UTF8, "application/json")
            };

            var httpResponse = await client.SendAsync(requestMessage);

            if (httpResponse.IsSuccessStatusCode)
            {
                var strResponse = await httpResponse.Content.ReadAsStringAsync();
                var jsonResponse = JsonNode.Parse(strResponse);

                if (jsonResponse != null)
                {
                    string paypalOrderId = jsonResponse["id"]?.ToString() ?? "";
                    return new JsonResult(new { Id = paypalOrderId });
                }
            }
            else
            {
                var error = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogError($"PayPal order creation failed: {error}");
            }

            return new JsonResult(new { Id = "" });
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        public async Task<IActionResult> ConfirmDeposit([FromBody] JsonObject Data)
        {
            var orderId = Data?["orderID"]?.ToString();
            var accountIdStr = Data?["accountId"]?.ToString();

            if (string.IsNullOrEmpty(accountIdStr) || !int.TryParse(accountIdStr, out int accountId))
            {
                return new JsonResult("invalid-account-id");
            }

            if (orderId == null)
            {
                return new JsonResult("error");
            }

            string accessToken = await GetPaypalAccessToken();

            string url = _PaypalUrl + "/v2/checkout/orders/" + orderId + "/capture";

            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json")
            };

            var httpResponse = await client.SendAsync(requestMessage);

            if (httpResponse.IsSuccessStatusCode)
            {
                var strResponse = await httpResponse.Content.ReadAsStringAsync();
                var jsonResponse = JsonNode.Parse(strResponse);

                if (jsonResponse != null)
                {
                    string paypalOrderId = jsonResponse["status"]?.ToString() ?? "";
                    if(paypalOrderId == "COMPLETED")
                    {

                        decimal depositAmount = 0;
                        try
                        {
                            var amountStr = jsonResponse["purchase_units"]?[0]?["payments"]?["captures"]?[0]?["amount"]?["value"]?.ToString();
                            if (!string.IsNullOrEmpty(amountStr))
                            {
                                depositAmount = decimal.Parse(amountStr, CultureInfo.InvariantCulture);
                                int bankAccountId = int.Parse(accountIdStr!);

                                ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

                                if (currentUser == null)
                                    return RedirectToAction("Login");

                                ApplicationUser? user = await _context.Users
                                    .Include(u => u.BankAccounts!)
                                    .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

                                if (user == null)
                                    return RedirectToAction("Login");

                                if (user != null && user.IsBanned)
                                {
                                    return RedirectToAction("Index");
                                }


                                ICollection<BankAccount> userAccounts = user.BankAccounts!;

                                BankAccount depositAcc = userAccounts.FirstOrDefault(u => u.Id == bankAccountId);

                                if (depositAcc == null)
                                {
                                    return new JsonResult("error");
                                }

                                depositAcc.Balance += depositAmount;

                                _context.Transactions.AddRange(new[]
                                {
                                    new Transaction
                                    {
                                        Amount = depositAmount,
                                        Type = TransactionType.Deposit,
                                        Description = $"Deposit",
                                        BankAccountId = depositAcc.Id,
                                        UserId = user.Id
                                    }
                                });

                                await _context.SaveChangesAsync();

                                DateTime utcNow = DateTime.UtcNow;

                                TimeZoneInfo pacificZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                                DateTime pacificTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, pacificZone);

                                string tzAbbr = pacificZone.IsDaylightSavingTime(pacificTime) ? "PDT" : "PST";

                                string formatted = pacificTime.ToString("MMMM d, yyyy 'at' h:mm tt") + $" {tzAbbr}";

                                await _mailJetService.SendConfirmedDeposit(user.PersonalEmail, depositAmount.ToString(), formatted);

                                return new JsonResult("success");

                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("Failed to parse PayPal amount: " + ex.Message);
                            return new JsonResult("error");
                        }

                        return new JsonResult("success");
                    }
                }
            }
            else
            {
                var error = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogError($"PayPal order creation failed: {error}");
            }

            return new JsonResult("error");
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> Index()
        {

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts!)
                .ThenInclude(a => a.Transactions)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            if (user == null)
                return RedirectToAction("Login");

            var latestTransactions = await _context.Transactions
                .Where(t => t.BankAccount.UserId == currentUser.Id) // adjust property path accordingly
                .OrderByDescending(t => t.Date)
                .Take(3)
                .Select(t => new TransactionVM
                {
                    AccountName = t.BankAccount.Type.ToString(),
                    Type = t.Type,
                    Amount = t.Amount,
                    Date = t.Date
                })
                .ToListAsync();

            decimal totalBalance = user.BankAccounts!.Sum(a => a.Balance);

            Dictionary<string, decimal> convertedBalances = await _marketDataService.GetConvertedBalancesAsync(totalBalance);

            var marketData = await _marketDataService.GetMarketDataAsync();

            AccountViewVM userView = new AccountViewVM
            {
                FullName = user.FullName!,
                TotalBalance = totalBalance,
                BankAccounts = user.BankAccounts!,
                Transactions = latestTransactions,
                ConvertedBalances = convertedBalances,
                MarketData = marketData,
                IsBanned = user.IsBanned,
                BanReason = user.BanReason,
            };

            if (user.IsBanned)
            {
                ViewBag.IsBanned = true;

                await _mailJetService.SendBannedUser(user.PersonalEmail, user.FullName);

            }

            return View(userView);
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> Balance()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            var user = await _context.Users
                .Include(u => u.BankAccounts!)
                .ThenInclude(a => a.Transactions)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            if (user == null)
                return RedirectToAction("Login");

            if (user.IsBanned)
                return RedirectToAction("Index");

            var latestTransactions = user.BankAccounts!
                .SelectMany(account => account.Transactions.Select(t => new TransactionVM
                {
                    AccountName = account.Type.ToString(),
                    Type = t.Type,
                    Amount = t.Amount,
                    Date = t.Date
                }))
                .OrderByDescending(t => t.Date)
                .Take(3)
                .ToList();

            DateTime oneMonthAgo = DateTime.UtcNow.AddMonths(-1);
            decimal monthlyInterestEarnings = user.BankAccounts!
                .SelectMany(a => a.Transactions)
                .Where(t => t.Type == TransactionType.Interest && t.Date >= oneMonthAgo)
                .Sum(t => t.Amount);

            decimal totalBalance = user.BankAccounts!.Sum(a => a.Balance);

            decimal savingsBalance = user.BankAccounts!
                .Where(a => a.Type.ToString() == "Savings")
                .Sum(a => a.Balance);

            var accountBalances = user.BankAccounts!
                .Select(a => new AccountBalanceVM
                {
                    AccountName = a.Type.ToString(),
                    Balance = a.Balance
                })
                .ToList();

            var summaryVM = new BalanceVM
            {
                LatestTransactions = latestTransactions,
                MonthlyInterestEarnings = monthlyInterestEarnings,
                TotalBalance = totalBalance,
                SavingsBalance = savingsBalance,
                AccountBalances = accountBalances
            };

            return View(summaryVM);
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> Activity()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts!)
                .ThenInclude(a => a.Transactions)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            if (user == null)
                return RedirectToAction("Login");

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }

            List<TransactionVM> allTransactions = user.BankAccounts!
                .SelectMany(account => account.Transactions.Select(t => new TransactionVM
                {
                    AccountName = account.Type.ToString(),
                    Type = t.Type,
                    Amount = t.Amount,
                    Date = t.Date,
                }))
                .OrderByDescending(t => t.Date)
                .ToList();

            var activity = new ActivityVM
            {
                Transactions = allTransactions,
            };

            return View(activity);
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> TransferFundsToInternalAccount()
        {

            ViewBag.HideBanner = true;

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }


            decimal totalBalance = user!.BankAccounts!.Sum(a => a.Balance);

            Dictionary<string, decimal> convertedBalance = await _marketDataService.GetConvertedBalancesAsync(totalBalance);

            InternalTransferFundsVM transferFundsVM = new InternalTransferFundsVM
            {
                BankAccounts = user!.BankAccounts,
                ConvertedBalances = convertedBalance,
            };

            ViewBag.HideBanner = true;

            return View(transferFundsVM);
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReviewInternalTransfer(InternalTransferFundsVM model)
        {
            ApplicationUser? user = await _userManager.GetUserAsync(User);

            if (user == null)
                return RedirectToAction("Login");

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }

            ICollection<BankAccount> bankAccounts = await _context.BankAccounts
                    .Where(a => a.UserId == user.Id)
                    .ToListAsync();

            bool isPinValid = await VerifyUserPinAsync(user, model.Pin);

            if (!ModelState.IsValid || !isPinValid)
            {
                model.BankAccounts = bankAccounts;

                ModelState.AddModelError(string.Empty, "Fill in all the required input fields correctly.");

                ViewBag.HideBanner = true;
                return View("TransferFundsToInternalAccount", model);
            }

            ViewBag.HideBanner = true;


            model.FromAccount = bankAccounts.FirstOrDefault(a => a.Id == model.FromAccountId);
            model.ToAccount = bankAccounts.FirstOrDefault(a => a.Id == model.ToAccountId);

            return View(model);
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmInternalTransfer(InternalTransferFundsVM model)
        {
            ViewBag.HideBanner = true;

            ApplicationUser? user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Unauthorized();
            }

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }

            List<BankAccount> bankAccounts = await _context.BankAccounts
                .Where(a => a.UserId == user.Id)
                .ToListAsync();

            model.BankAccounts = bankAccounts;

            if (!ModelState.IsValid)
            {
                return View("TransferFundsToInternalAccount", model);
            }

            // Find the source account
            BankAccount? fromAccount = bankAccounts.FirstOrDefault(a => a.Id == model.FromAccountId);
            if (fromAccount == null)
            {
                ModelState.AddModelError(string.Empty, "Selected source account not found.");
                return View("TransferFundsToInternalAccount", model);
            }

            // Example: Verify the PIN (assuming you have a way to verify it securely)
            bool isPinValid = await VerifyUserPinAsync(user, model.Pin);
            if (!isPinValid)
            {
                ModelState.AddModelError(nameof(model.Pin), "Invalid security PIN.");
                return View("TransferFundsToInternalAccount", model);
            }

            // Check sufficient balance
            if (fromAccount.Balance < model.Amount)
            {
                ModelState.AddModelError(string.Empty, "Insufficient funds in the source account.");
                return View("TransferFundsToInternalAccount", model);
            }

            if (model.Amount <= 0)
            {
                ModelState.AddModelError(string.Empty, "Invalid transfer amount.");
                return View("TransferFundsToInternalAccount", model);
            }

            if (model.FromAccountId == model.ToAccountId)
            {
                ModelState.AddModelError("", "You must choose different accounts.");
                return View("TransferFundsToInternalAccount", model);
            }

            List<BankAccount> accounts = await _context.BankAccounts
                .Where(a => a.UserId == user.Id && (a.Id == model.FromAccountId || a.Id == model.ToAccountId))
                .ToListAsync();

            BankAccount? toAccount = accounts.FirstOrDefault(a => a.Id == model.ToAccountId);

            if (fromAccount == null || toAccount == null)
            {
                ModelState.AddModelError("", "One or both accounts were not found.");
                return View("TransferFundsToInternalAccount", model);
            }
            if (fromAccount == null || toAccount == null)
            {
                ModelState.AddModelError("", "One or both accounts were not found.");
                TempData["ActiveTab"] = "Transfer";
                return View("TransferFundsToInternalAccount", model);
            }

            const decimal internalTransferFee = 5.00m;

            // Check for sufficient balance (amount + fee)
            if (fromAccount.Balance < model.Amount + internalTransferFee)
            {
                ModelState.AddModelError(string.Empty, $"Insufficient funds (transfer + ${internalTransferFee} fee).");
                return View("TransferFundsToInternalAccount", model);
            }

            fromAccount.Balance -= (model.Amount + internalTransferFee);
            toAccount.Balance += model.Amount;

            _context.Transactions.AddRange(new[]
            {
                new Transaction
                {
                    Amount = model.Amount,
                    Type = TransactionType.Transfer,
                    Description = $"Transfer to {toAccount.Type} account",
                    BankAccountId = fromAccount.Id,
                    UserId = user.Id,
                    Date = DateTime.UtcNow
                },
                new Transaction
                {
                    Amount = model.Amount,
                    Type = TransactionType.Deposit,
                    Description = $"Transfer from {fromAccount.Type} account",
                    BankAccountId = toAccount.Id,
                    UserId = user.Id,
                    Date = DateTime.UtcNow
                },
                new Transaction
                {
                    Amount = internalTransferFee,
                    Type = TransactionType.ServiceFee,
                    Description = "Internal Transfer Fee",
                    BankAccountId = fromAccount.Id,
                    UserId = user.Id,
                    Date = DateTime.UtcNow,
                    Purpose = PurposeType.Other
                }
            });

            await _context.SaveChangesAsync();

            TempData["InternalTransferModal"] = "Active";

            return RedirectToAction("Index");
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> TransferFundsToExternalAccount()
        {
            ViewBag.HideBanner = true;

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            var purposeList = Enum.GetValues(typeof(PurposeType))
                .Cast<PurposeType>()
                .Select(e => new SelectListItem
                {
                    Value = e.ToString(),
                    Text = Regex.Replace(e.ToString(), "(\\B[A-Z])", " $1")
                })
                .ToList();

            ViewBag.PurposeList = purposeList;

            ExternalTransferFundsVM transferFundsVM = new ExternalTransferFundsVM
            {
                BankAccounts = user!.BankAccounts
            };

            return View(transferFundsVM);
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReviewExternalTransfer(ExternalTransferFundsVM model)
        {
            ApplicationUser? user = await _userManager.GetUserAsync(User);

            if (user == null)
                return RedirectToAction("Login");

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }

            ICollection<BankAccount> bankAccounts = await _context.BankAccounts
                    .Where(a => a.UserId == user.Id)
                    .ToListAsync();

            bool isPinValid = await VerifyUserPinAsync(user, model.Pin);

            if (!ModelState.IsValid || !isPinValid)
            {
                model.BankAccounts = bankAccounts;

                var purposeList = Enum.GetValues(typeof(PurposeType))
               .Cast<PurposeType>()
               .Select(e => new SelectListItem
               {
                   Value = e.ToString(),
                   Text = Regex.Replace(e.ToString(), "(\\B[A-Z])", " $1")
               })
               .ToList();

                ViewBag.PurposeList = purposeList;

                ModelState.AddModelError(string.Empty, "Fill in all the required input fields correctly.");

                return View("TransferFundsToExternalAccount", model);
            }

            ViewBag.HideBanner = true;

            model.FromAccount = bankAccounts.FirstOrDefault(a => a.Id == model.AccountId);

            return View(model);
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmExternalTransfer(ExternalTransferFundsVM model)
        {
            ViewBag.HideBanner = true;

            ApplicationUser? user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Unauthorized();
            }

            if (user != null && user.IsBanned)
            {
                return RedirectToAction("Index");
            }

            var privilegedUsers = new[] { "TestClient123", "MichaelDavidCox", "ThomasLDille", "FrederickEWidlund" };
            bool isPrivilegedUser = User.Identity != null && User.Identity.IsAuthenticated &&
                                    privilegedUsers.Contains(user.UserName);

            List<BankAccount> bankAccounts = await _context.BankAccounts
                .Where(a => a.UserId == user.Id)
                .ToListAsync();

            model.BankAccounts = bankAccounts;

            if (!ModelState.IsValid)
            {
                return View("TransferFundsToExternalAccount", model);
            }

            // Find the source account
            BankAccount? fromAccount = bankAccounts.FirstOrDefault(a => a.Id == model.AccountId);
            if (fromAccount == null)
            {
                ModelState.AddModelError(string.Empty, "Selected source account not found.");
                return View("TransferFundsToExternalAccount", model);
            }

            // Example: Verify the PIN (assuming you have a way to verify it securely)
            bool isPinValid = await VerifyUserPinAsync(user, model.Pin);
            if (!isPinValid)
            {
                ModelState.AddModelError(nameof(model.Pin), "Invalid security PIN.");
                return View("TransferFundsToExternalAccount", model);
            }

            const decimal externalTransferFee = 50.00m;

            if (fromAccount.Balance < model.Amount + externalTransferFee)
            {
                ModelState.AddModelError(string.Empty, $"Insufficient funds (transfer + ${externalTransferFee} fee).");
                return View("TransferFundsToExternalAccount", model);
            }

            if (model.Amount <= 0)
            {
                ModelState.AddModelError(string.Empty, "Invalid transfer amount.");
                return View("TransferFundsToExternalAccount", model);
            }

            // Deduct total (amount + fee)
            fromAccount.Balance -= (model.Amount + externalTransferFee);

            // Main transfer transaction
            Transaction transaction = new Transaction
            {
                Amount = model.Amount,
                Type = Enum.Parse<TransactionType>(model.TransferType),
                Description = model.Description ?? $"Transfer to {model.ToAccountNumber}",
                BankAccountId = fromAccount.Id,
                UserId = user.Id,
                Date = DateTime.UtcNow,
                ToAccountNumber = model.ToAccountNumber,
                Recipient = model.RecipientName,
                Purpose = Enum.Parse<PurposeType>(model.Purpose),
            };

            Transaction feeTransaction = new Transaction
            {
                Amount = externalTransferFee,
                Type = TransactionType.ServiceFee,
                Description = "External Transfer Fee",
                BankAccountId = fromAccount.Id,
                UserId = user.Id,
                Date = DateTime.UtcNow,
                Purpose = PurposeType.Other
            };

            _context.Transactions.AddRange(transaction, feeTransaction);

            await _context.SaveChangesAsync();

            if (isPrivilegedUser)
            {
                var random = new Random();
                int delayMinutes = random.Next(5, 11);

                DateTime utcNow = DateTime.UtcNow;

                TimeZoneInfo pacificZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                DateTime pacificTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, pacificZone);

                string tzAbbr = pacificZone.IsDaylightSavingTime(pacificTime) ? "PDT" : "PST";

                string formatted = pacificTime.ToString("MMMM d, yyyy 'at' h:mm tt") + $" {tzAbbr}";

                BackgroundJob.Schedule(
                    () => _mailJetService.SendExternalTransfer(user.PersonalEmail, formatted, model.Amount.ToString(), user.FullName),
                    TimeSpan.FromMinutes(delayMinutes)
                );
            }

            TempData["ExternalTransferModal"] = "Active";

            return RedirectToAction("Index");
        }

        //LoginKey
        public IActionResult Login()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Login(LoginVM model)
        {
            if (ModelState.IsValid)
            {
                Microsoft.AspNetCore.Identity.SignInResult result = await _signInManager.PasswordSignInAsync(model.Username, model.Password, true, lockoutOnFailure: true);

                if (result.Succeeded)
                {
                    ApplicationUser? user = await _userManager.FindByNameAsync(model.Username);

                    var session = new UserSession
                    {
                        UserId = user.Id,
                        ExpiresAt = DateTime.UtcNow.AddHours(12),
                        IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                        UserAgent = HttpContext.Request.Headers["User-Agent"].ToString(),
                    };

                    _context.UserSessions.Add(session);
                    await _context.SaveChangesAsync();

                    Response.Cookies.Append("auth_session_id", session.SessionId.ToString(), new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.None,
                        Secure = true,
                        Expires = session.ExpiresAt
                    });

                    var roles = await _userManager.GetRolesAsync(user!);

                    // Redirect based on roles
                    if (roles.Contains("Admin"))
                        return RedirectToAction("Index", "Home");

                    if (roles.Contains("Client"))
                        return RedirectToAction("Index", "Account");
                }
                else
                {
                    _context.FailedLoginLog.Add(new FailedLoginLog
                    {
                        EmailOrUsername = model.Username,
                        AttemptedAt = DateTime.UtcNow,
                        IPAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown"
                    });

                    await _context.SaveChangesAsync();
                }
            }

            ModelState.AddModelError(string.Empty, "Incorrect username or password.");
            return View(model);
        }

        public async Task<ActionResult> Logout()
        {
            await _signInManager.SignOutAsync();

            var sessionId = Request.Cookies["auth_session_id"];
            if (Guid.TryParse(sessionId, out var id))
            {
                var session = await _context.UserSessions.FirstOrDefaultAsync(s => s.SessionId == id.ToString());
                if (session != null)
                {
                    session.IsActive = false;
                    await _context.SaveChangesAsync();
                }
            }

            Response.Cookies.Delete("auth_session_id");

            return RedirectToAction("Login", "Account");
        }

        private async Task<bool> VerifyUserPinAsync(ApplicationUser user, string pin)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrEmpty(user?.PasswordHash))
                    return false;

                var passwordHasher = new PasswordHasher<ApplicationUser>();
                var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, pin);
                return result == PasswordVerificationResult.Success;
            });
        }

        [Authorize(Roles = "Client")]
        public IActionResult ResetPassword()
        {
            ViewBag.HideBanner = true;

            return View();
        }

        [Authorize(Roles = "Client")]
        [HttpPost]
        public async Task<IActionResult> ResetPassword(ResetPasswordVM model)
        {

            ViewBag.HideBanner = true;

            if (!ModelState.IsValid)
                return View(model);

            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null)
            {
                ModelState.AddModelError(string.Empty, "User not found.");
                return View(model);
            }

            if (currentUser != null && currentUser.IsBanned)
            {
                return RedirectToAction("Index");
            }

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts!)
                .ThenInclude(a => a.Transactions)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            const decimal fee = 5.00m;

            var orderedAccounts = user.BankAccounts
                .OrderBy(a =>
                    a.Type == BankAccount.AccountType.Checking ? 0 :
                    a.Type == BankAccount.AccountType.Savings ? 1 :
                    a.Type == BankAccount.AccountType.TrustFund ? 2 : 3)
                .ToList();

            foreach (var account in orderedAccounts)
            {
                if (account.Balance >= fee)
                {

                    if (model.NewPassword != model.NewPasswordConfirm)
                    {
                        ModelState.AddModelError(string.Empty, "New passwords do not match.");
                        return View(model);
                    }

                    if (string.IsNullOrWhiteSpace(model.NewPassword) ||
                        model.NewPassword.Length < 8 ||
                        !model.NewPassword.Any(char.IsLower) ||
                        !model.NewPassword.Any(char.IsUpper) ||
                        !model.NewPassword.Any(char.IsDigit) ||
                        !model.NewPassword.Any(c => !char.IsLetterOrDigit(c))
                    )
                    {
                        ModelState.AddModelError(string.Empty, "Password must be at least 8 characters long and include at least one lowercase letter, one uppercase letter, one number, and one special character.");
                        return View(model);
                    }

                    var result = await _userManager.ChangePasswordAsync(user, model.OldPassword, model.NewPassword);
                    if (!result.Succeeded)
                    {
                        foreach (var error in result.Errors)
                        {
                            ModelState.AddModelError(string.Empty, error.Description);
                        }
                        return View(model);
                    }

                    account.Balance -= fee;

                    account.Transactions.Add(new Transaction
                    {
                        Amount = fee,
                        Type = TransactionType.ServiceFee,
                        Description = "Password Change Fee",
                        BankAccountId = account.Id,
                        UserId = user.Id,
                        Date = DateTime.UtcNow,
                        Purpose = PurposeType.Other
                    });

                    await _context.SaveChangesAsync();

                    await _mailJetService.SendUpdatedCredentials(user.PersonalEmail, user.UserName, model.NewPassword);

                    return RedirectToAction("Index");

                }
            }

            ModelState.AddModelError(string.Empty, $"Insufficient funds (${fee} USD fee).");
            return View("ResetPassword", model);

        }

        [HttpGet]
        public IActionResult DownloadClientPdf()
        {
            var relativePath = Path.Combine("App_Data", "OurClients", "Guardian Capitol - Clients.pdf");
            var filePath = Path.Combine(_env.ContentRootPath, relativePath);

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/pdf";
            }

            var fileName = Path.GetFileName(filePath);
            return PhysicalFile(filePath, contentType, fileName);
        }

        [HttpGet]
        [Authorize(Roles = "Client")]
        public IActionResult Download8300()
        {
            var relativePath = Path.Combine("App_Data", "Forms", "f8300.pdf");
            var filePath = Path.Combine(_env.ContentRootPath, relativePath);

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/pdf";
            }

            var fileName = Path.GetFileName(filePath);
            return PhysicalFile(filePath, contentType, fileName);
        }

        [HttpGet]
        [Authorize(Roles = "Client")]
        public IActionResult DownloadT3()
        {
            var relativePath = Path.Combine("App_Data", "Forms", "t3-fill-24e.pdf");
            var filePath = Path.Combine(_env.ContentRootPath, relativePath);

            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/pdf";
            }

            var fileName = Path.GetFileName(filePath);
            return PhysicalFile(filePath, contentType, fileName);
        }

        [Authorize(Roles = "Client")]
        public async Task<IActionResult> GenerateStatement()
        {
            ViewBag.HideBanner = true;
            ViewBag.Hide = true;

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
                return RedirectToAction("Login");

            ApplicationUser? user = await _context.Users
                .Include(u => u.BankAccounts!)
                .ThenInclude(a => a.Transactions)
                .FirstOrDefaultAsync(u => u.Id == currentUser.Id);

            if (user == null)
                return RedirectToAction("Login");

            List<TransactionVM> allTransactions = user.BankAccounts!
                .SelectMany(account => account.Transactions.Select(t => new TransactionVM
                {
                    AccountName = account.Type.ToString(),
                    Type = t.Type,
                    Amount = t.Amount,
                    Date = t.Date,
                }))
                .OrderByDescending(t => t.Date)
                .Take(10)
                .ToList();

            decimal totalBalance = user.BankAccounts!.Sum(a => a.Balance);

            Dictionary<string, decimal> convertedBalances = await _marketDataService.GetConvertedBalancesAsync(totalBalance);

            PrintProfileVM userView = new PrintProfileVM
            {
                FullName = user.FullName!,
                TotalBalance = totalBalance,
                BankAccounts = user.BankAccounts!,
                Transactions = allTransactions,
                ConvertedBalances = convertedBalances,
            };

            if (user.IsBanned)
            {
                ViewBag.IsBanned = true;

                await _mailJetService.SendBannedUser(user.PersonalEmail, user.FullName);

            }

            return View(userView);
        }

    }
}
