using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Util.Store;
using log4net;
using Newtonsoft.Json.Linq;
using OutlookGoogleCalendarSync.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ogcs = OutlookGoogleCalendarSync;

namespace OutlookGoogleCalendarSync.Google {
    public class Authenticator {
        private static readonly ILog log = LogManager.GetLogger(typeof(Authenticator));

        private string[] requiredScopes = new[] {
                CalendarService.ScopeConstants.Calendar,
                "email" };

        private Boolean authenticated = false;
        public Boolean Authenticated { get { return authenticated; } }
        private String GrantedScopes;
        public Boolean SufficientPermissions {
            get {
                if (string.IsNullOrEmpty(GrantedScopes)) return false;
                foreach (String s in requiredScopes) {
                    if (!GrantedScopes.Contains(s)) return false;
                }
                return true; 
            }
        }

        public const String TokenFile = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user";
        private String tokenFullPath;
        private Boolean tokenFileExists { get { return File.Exists(tokenFullPath); } }

        public System.Threading.CancellationTokenSource CancelTokenSource;

        public Authenticator() { }

        public void GetAuthenticated() {
            if (this.authenticated) return;

            CancelTokenSource = new System.Threading.CancellationTokenSource();
            Forms.Main.Instance.Console.Update("<span class='em em-key'></span>Authenticating with Google", Console.Markup.h2, newLine: false, verbose: true);

            System.Threading.Thread oAuth = new System.Threading.Thread(() => { spawnOauth(); });
            oAuth.Start();
            while (oAuth.IsAlive) {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(100);
                if (CancelTokenSource.IsCancellationRequested) break;
            }
        }

        private void spawnOauth() {
            try {
                ClientSecrets cs = getCalendarClientSecrets();
                //Calling an async function from a static constructor needs to be called like this, else it deadlocks:-
                Task task = Task.Run(async () => { await getAuthenticated(cs); });
                try {
                    task.Wait(CancelTokenSource.Token);
                } catch (System.OperationCanceledException) {
                    Forms.Main.Instance.Console.Update("Authorisation to allow OGCS to manage your Google calendar was cancelled.", Console.Markup.warning);
                } catch (System.Exception ex) {
                    ex.Analyse();
                }
            } catch (System.Exception ex) {
                log.Fail("Problem encountered in getCalendarClientSecrets()");
                Forms.Main.Instance.Console.UpdateWithError("Unable to authenticate with Google!", ex);
            }
        }

        private static ClientSecrets getCalendarClientSecrets() {
            ClientSecrets provider = new ClientSecrets();
            if (Settings.Instance.UsingPersonalAPIkeys()) {
                provider.ClientId = Settings.Instance.PersonalClientIdentifier;
                provider.ClientSecret = Settings.Instance.PersonalClientSecret;
            } else {
                ApiKeyring apiKeyring = new ApiKeyring();

                apiKeyring.PickKey(ApiKeyring.KeyType.Standard);

                provider.ClientId = apiKeyring.Key.ClientId;
                provider.ClientSecret = apiKeyring.Key.ClientSecret;
            }
            return provider;
        }

        private async Task<bool> getAuthenticated(ClientSecrets cs) {
            log.Debug("Authenticating with Google calendar service...");

            FileDataStore tokenStore = new FileDataStore(Program.UserFilePath);
            tokenFullPath = Path.Combine(tokenStore.FolderPath, TokenFile);

            log.Debug("Google credential file location: " + Program.MaskFilePath(tokenFullPath));
            Boolean newlyAuthorised = false;
            if (newlyAuthorised = !tokenFileExists)
                log.Info("No Google credentials file available - need user authorisation for OGCS to manage their calendar.");
            
            UserCredential credential = null;
            try {
                if (newlyAuthorised = (authenticated && !SufficientPermissions)) File.Delete(tokenFullPath);

                //This will open the authorisation process in a browser, if required
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(cs, requiredScopes, "user", CancelTokenSource.Token, tokenStore);
                if (tokenFileExists) {
                    log.Debug("User has completed Google authorisation and credential file saved. Checking granular permissions...");

                    GrantedScopes = credential.Token.Scope;
                    if (!SufficientPermissions) {
                        log.Warn("They have not granted permission to the calendar.");

                        String noAuthGiven = "Sorry, but this application will not work if you don't allow it access to your Google Calendar :(\r\n\r\n" +
                            "Please disconnect the Google account and retrieve the calendars again.";
                        Ogcs.Extensions.MessageBox.Show(noAuthGiven, "Authorisation not given", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        throw new ApplicationException(noAuthGiven);
                    }
                    if (newlyAuthorised)
                        Forms.Main.Instance.Console.Update("Access authorised.", verbose: true);
                }

            } catch (global::Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex) {
                //Ogcs.Exception.AnalyseTokenResponse(ex);
                if (ex.Error.Error == "access_denied") {
                    String noAuthGiven = "Sorry, but this application will not work if you don't allow it access to your Google calendar.";
                    log.Warn("User did not provide authorisation code. Sync will not be able to work.");
                    Ogcs.Extensions.MessageBox.Show(noAuthGiven, "Authorisation not provided", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    throw new ApplicationException(noAuthGiven);
                } else {
                    Forms.Main.Instance.Console.UpdateWithError("Unable to authenticate with Google. The following error occurred:", ex);
                }

            } catch (OperationCanceledException) {
                Forms.Main.Instance.Console.Update("Unable to authenticate with Google. The operation was cancelled.", Console.Markup.warning);

            } catch (ApplicationException ex) {
                Forms.Main.Instance.Console.UpdateWithError(null, ex.LogAsFail());
                return false;

            } catch (System.Exception ex) {
                Ogcs.Exception.Analyse(ex);
                Forms.Main.Instance.Console.UpdateWithError("Unable to authenticate with Google. The following error occurred:", ex);
            }

            if (credential == null) return false;

            if (credential.Token.AccessToken != "" && credential.Token.RefreshToken != "") {
                log.Info("Refresh and Access token successfully retrieved.");
                log.Debug("Access token expires " + credential.Token.IssuedUtc.AddSeconds(credential.Token.ExpiresInSeconds.Value).ToLocalTime().ToString());
            }

            Ogcs.Google.Calendar.Instance.Service = new CalendarService(new global::Google.Apis.Services.BaseClientService.Initializer() { HttpClientInitializer = credential });
            Ogcs.Google.Calendar.Instance.Service.HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(Settings.Instance.Proxy.BrowserUserAgent);

            if (credential.Token.IssuedUtc.AddSeconds(credential.Token.ExpiresInSeconds.Value) < System.DateTime.UtcNow.AddMinutes(1)) {
                log.Debug("Access token needs refreshing.");
                //This will happen automatically when using the calendar service
                //But we need a valid token before we call getGaccountEmail() which doesn't use the service
                int backoff = 0;
                while (backoff < Calendar.BackoffLimit) {
                    try {
                        Ogcs.Google.Calendar.Instance.Service.Settings.Get("useKeyboardShortcuts").Execute();
                        break;
                    } catch (global::Google.GoogleApiException ex) {
                        switch (Calendar.HandleAPIlimits(ref ex, null)) {
                            case Calendar.ApiException.throwException: throw;
                            case Calendar.ApiException.freeAPIexhausted:
                                Ogcs.Exception.LogAsFail(ref ex);
                                Ogcs.Exception.Analyse(ex);
                                System.ApplicationException aex = new System.ApplicationException(Calendar.Instance.SubscriptionInvite, ex);
                                Ogcs.Exception.LogAsFail(ref aex);
                                authenticated = false;
                                return authenticated;
                        case Calendar.ApiException.backoffThenRetry:
                                backoff++;
                                if (backoff == Calendar.BackoffLimit) {
                                    log.Fail("API limit backoff was not successful. Retrieving useKeyboardShortcuts setting failed.");
                                    authenticated = false;
                                    return authenticated;
                                } else {
                                    log.Warn("API rate limit reached. Backing off " + backoff + "sec before retry.");
                                    System.Threading.Thread.Sleep(backoff * 1000);
                                }
                                break;
                        }

                    } catch (System.Exception ex) {
                        if (ex is global::Google.Apis.Auth.OAuth2.Responses.TokenResponseException)
                            Ogcs.Exception.AnalyseTokenResponse(ex as global::Google.Apis.Auth.OAuth2.Responses.TokenResponseException, false);
                        else {
                            Ogcs.Exception.Analyse(ex);
                            Forms.Main.Instance.Console.Update("Unable to communicate with Google services. " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message), Console.Markup.warning);
                        }
                        authenticated = false;
                        return authenticated;
                    }
                }
                log.Debug("Access token refreshed.");
            }

            getGaccountEmail(credential.Token.AccessToken);
            authenticated = true;
            Forms.Main.Instance.Console.Update("Handshake successful.", verbose: true);
            return authenticated;
        }

        public void Reset(Boolean reauthorise = true) {
            log.Info("Resetting Google Calendar authentication details.");
            Settings.Instance.AssignedClientIdentifier = "";
            Settings.Instance.GaccountEmail = "";
            authenticated = false;
            if (tokenFileExists) File.Delete(tokenFullPath);
            if (!Ogcs.Google.Calendar.IsInstanceNull) {
                Ogcs.Google.Calendar.Instance.Authenticator = new Authenticator();
                Ogcs.Google.Calendar.Instance.Service = null;
                if (reauthorise) {
                    Ogcs.Google.Calendar.Instance.Authenticator.GetAuthenticated();
                }
            }
        }

        private Int16 getEmailAttempts = 0;
        private void getGaccountEmail(String accessToken) {
            String jsonString = "";
            log.Debug("Retrieving email address associated with Google account.");
            try {
                jsonString = new Extensions.OgcsWebClient().DownloadString("https://www.googleapis.com/oauth2/v2/userinfo?fields=email&access_token=" + accessToken);
                JObject jo = Newtonsoft.Json.Linq.JObject.Parse(jsonString);
                JToken jtEmail = jo["email"];
                String email = jtEmail.ToString();

                if (Settings.Instance.GaccountEmail != email) {
                    if (!String.IsNullOrEmpty(Settings.Instance.GaccountEmail))
                        log.Debug("Looks like the Google account username value has been tampering with? :-O");
                    Settings.Instance.GaccountEmail = email;
                    log.Debug("Updating Google account username: " + Settings.Instance.GaccountEmail_masked());
                }
                getEmailAttempts = 0;
            } catch (System.Net.WebException ex) {
                getEmailAttempts++;
                if (ex.InnerException != null) log.Error("Inner exception: "+ ex.InnerException.Message);
                if (ex.Response != null) {
                    log.Debug("Reading response.");
                    System.IO.Stream stream = ex.Response.GetResponseStream();
                    System.IO.StreamReader sr = new System.IO.StreamReader(stream);
                    log.Error(sr.ReadToEnd());
                }
                if (ex.GetErrorCode() == "0x80131509") {
                    log.Warn(ex.Message);
                    System.Text.RegularExpressions.Regex rgx = new System.Text.RegularExpressions.Regex(@"\b(403|Forbidden|Prohibited|Insufficient Permission)\b", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (rgx.IsMatch(ex.Message)) {
                        if (Settings.Instance.UsingPersonalAPIkeys()) {
                            String msg = "If you are using your own API keys, you must also enable the Google+ API.";
                            Ogcs.Extensions.MessageBox.Show(msg, "Missing API Service", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Exclamation);
                            throw new System.ApplicationException(msg);
                        } else {
                            if (getEmailAttempts > 1) {
                                log.Error("Failed to retrieve Google account username.");
                                log.Debug("Using previously retrieved username: " + Settings.Instance.GaccountEmail_masked());
                            } else {
                                if ((new ApiKey.DefaultKey(ApiKeyring.KeyType.Standard)).ClientId == Settings.Instance.AssignedClientIdentifier) {
                                    Ogcs.Extensions.MessageBox.Show(ex.Message + "\r\n\r\nPlease check your internet connection and any relevant proxy configuration.",
                                        "Unable to communicate with Google", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Exclamation);
                                    throw;
                                } else {
                                    ApiKeyring.ChangeKeys();
                                    return;
                                }
                            }
                        }
                    } else {
                        Forms.Main.Instance.Console.UpdateWithError("", (ex.InnerException ?? ex));
                        throw;
                    }
                }
                Ogcs.Exception.Analyse(ex);
                if (ex.Message.ToLower().Contains("access denied")) {
                    Forms.Main.Instance.Console.Update("Failed to obtain Calendar access from Google - it's possible your access has been revoked."
                       + "<br/>Try disconnecting your Google account and reauthorising OGCS.", Console.Markup.error);
                } else if (ex.Message.ToLower().Contains("prohibited") && Settings.Instance.UsingPersonalAPIkeys()) {
                    Forms.Main.Instance.Console.Update("If you are using your own API keys, you must also enable the Google+ API.", Console.Markup.warning);
                }
                throw;

            } catch (System.Exception ex) {
                log.Debug("JSON: " + jsonString);
                log.Error("Failed to retrieve Google account username.");
                Ogcs.Exception.Analyse(ex);
                log.Debug("Using previously retrieved username: " + Settings.Instance.GaccountEmail_masked());
            }
        }

        public static String GetMd5(String input, Boolean isEmailAddress = false, Boolean silent = false) {
            if (input == null) return null;
            if (!silent) log.Debug("Getting MD5 hash for '" + (isEmailAddress ? EmailAddress.MaskAddress(input) : input) + "'");
            if (string.IsNullOrEmpty(input)) return "";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();

            try {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
                byte[] hash = md5.ComputeHash(inputBytes);

                //Convert byte array to hex string
                for (int i = 0; i < hash.Length; i++) {
                    sb.Append(hash[i].ToString("x2"));
                }
            } catch (System.Exception ex) {
                log.Error("Failed to create MD5" + (silent ? "." : " for '" + (isEmailAddress ? EmailAddress.MaskAddress(input) : input) + "'."));
                Ogcs.Exception.Analyse(ex);
            }
            return sb.ToString();
        }

    }
}
