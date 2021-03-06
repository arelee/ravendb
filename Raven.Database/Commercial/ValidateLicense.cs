//-----------------------------------------------------------------------
// <copyright file="ValidateLicense.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Database.Config;
using Raven.Database.Impl.Clustering;
using Raven.Database.Plugins;
using Rhino.Licensing;
using Rhino.Licensing.Discovery;
using Raven.Database.Extensions;

namespace Raven.Database.Commercial
{
	internal class ValidateLicense : IDisposable
	{
		public static LicensingStatus CurrentLicense { get; set; }
		public static IDictionary<string, string> LicenseAttributes { get; set; }
		private AbstractLicenseValidator licenseValidator;
		private readonly ILog logger = LogManager.GetCurrentClassLogger();
		private Timer timer;

		static ValidateLicense()
		{
			LicenseAttributes = new Dictionary<string, string>();
			CurrentLicense = new LicensingStatus
			{
				Status = "AGPL - Open Source",
				Error = false,
				Message = "No license file was found.\r\n" +
				          "The AGPL license restrictions apply, only Open Source / Development work is permitted."
			};
		}

		public void Execute(InMemoryRavenConfiguration config)
		{
			timer = new Timer(state => ExecuteInternal(config), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

			ExecuteInternal(config);
		}

		private void ExecuteInternal(InMemoryRavenConfiguration config)
		{
			var licensePath = GetLicensePath(config);
			var licenseText = GetLicenseText(config);
			
			if (TryLoadLicense(licenseText) == false) 
				return;

			try
			{
				licenseValidator.AssertValidLicense(() =>
				{
					string value;

					LicenseAttributes = licenseValidator.LicenseAttributes;

					AssertForV2(licenseValidator.LicenseAttributes);
					if (licenseValidator.LicenseAttributes.TryGetValue("OEM", out value) &&
					    "true".Equals(value, StringComparison.InvariantCultureIgnoreCase))
					{
						licenseValidator.MultipleLicenseUsageBehavior = AbstractLicenseValidator.MultipleLicenseUsage.AllowSameLicense;
					}
				});

				CurrentLicense = new LicensingStatus
				{
					Status = "Commercial - " + licenseValidator.LicenseType,
					Error = false,
					Message = "Valid license at " + licensePath
				};
			}
			catch (Exception e)
			{
				logger.ErrorException("Could not validate license at " + licensePath + ", " + licenseText, e);

				CurrentLicense = new LicensingStatus
				{
					Status = "AGPL - Open Source",
					Error = true,
					Message = "Could not validate license: " + licensePath + ", " + licenseText + Environment.NewLine + e
				};
			}
		}

		private bool TryLoadLicense(string licenseText)
		{
			string publicKey;
			using (
				var stream = typeof (ValidateLicense).Assembly.GetManifestResourceStream("Raven.Database.Commercial.RavenDB.public"))
			{
				if (stream == null)
					throw new InvalidOperationException("Could not find public key for the license");
				publicKey = new StreamReader(stream).ReadToEnd();
			}


			licenseValidator = new StringLicenseValidator(publicKey, licenseText)
			{
				DisableFloatingLicenses = true,
				SubscriptionEndpoint = "http://uberprof.com/Subscriptions.svc"
			};
			licenseValidator.LicenseInvalidated += OnLicenseInvalidated;
			licenseValidator.MultipleLicensesWereDiscovered += OnMultipleLicensesWereDiscovered;

			if (string.IsNullOrEmpty(licenseText))
			{
				CurrentLicense = new LicensingStatus
				{
					Status = "AGPL - Open Source",
					Error = false,
					Message = "No license file was found at " + licenseText +
					          "\r\nThe AGPL license restrictions apply, only Open Source / Development work is permitted."
				};
				return false;
			}
			return true;
		}

		private void AssertForV2(IDictionary<string, string> licenseAttributes)
		{
			string version;
			if (licenseAttributes.TryGetValue("version", out version) == false)
				throw new LicenseExpiredException("This is not a licence for RavenDB 1.2");

			if(version != "1.2")
				throw new LicenseExpiredException("This is not a licence for RavenDB 1.2");

			string maxRam;
			if (licenseAttributes.TryGetValue("maxRamUtilization", out maxRam))
			{
				if (string.Equals(maxRam, "unlimited",StringComparison.InvariantCultureIgnoreCase) == false)
				{
					MemoryStatistics.MemoryLimit = int.Parse(maxRam);
				}
			}
			
			string maxParallel;
			if (licenseAttributes.TryGetValue("maxParallelism", out maxParallel))
			{
				if (string.Equals(maxParallel, "unlimited", StringComparison.InvariantCultureIgnoreCase) == false)
				{
					MemoryStatistics.MaxParallelism = int.Parse(maxParallel);
				}
			}

			var clasterInspector = new ClusterInspecter();

			string claster;
			if (licenseAttributes.TryGetValue("allowWindowsClustering", out claster))
			{
				if(bool.Parse(claster) == false)
				{
					if (clasterInspector.IsRavenRunningAsClusterGenericService())
						throw new InvalidOperationException("Your license does not allow clastering, but RavenDB is running in clustered mode");
				}
			}
			else
			{
				if (clasterInspector.IsRavenRunningAsClusterGenericService())
					throw new InvalidOperationException("Your license does not allow clastering, but RavenDB is running in clustered mode");
			}
		}

		private static string GetLicenseText(InMemoryRavenConfiguration config)
		{
			var value = config.Settings["Raven/License"];
			if (string.IsNullOrEmpty(value) == false)
				return value;
			var fullPath = GetLicensePath(config).ToFullPath();
			if (File.Exists(fullPath))
				return File.ReadAllText(fullPath);
			return string.Empty;
		}

		private static string GetLicensePath(InMemoryRavenConfiguration config)
		{
			var value = config.Settings["Raven/License"];
			if (string.IsNullOrEmpty(value) == false)
				return "configuration";
			value = config.Settings["Raven/LicensePath"];
			if (string.IsNullOrEmpty(value) == false)
				return value;
			return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "license.xml");
		}

		private void OnMultipleLicensesWereDiscovered(object sender, DiscoveryHost.ClientDiscoveredEventArgs clientDiscoveredEventArgs)
		{
			logger.Error("A duplicate license was found at {0} for user {1}. User Id: {2}. Both licenses were disabled!", 
				clientDiscoveredEventArgs.MachineName, 
				clientDiscoveredEventArgs.UserName, 
				clientDiscoveredEventArgs.UserId);

			CurrentLicense = new LicensingStatus
			{
				Status = "AGPL - Open Source",
				Error = true,
				Message =
					string.Format("A duplicate license was found at {0} for user {1}. User Id: {2}.", clientDiscoveredEventArgs.MachineName,
					              clientDiscoveredEventArgs.UserName,
					              clientDiscoveredEventArgs.UserId)
			};
		}

		private void OnLicenseInvalidated(InvalidationType invalidationType)
		{
			logger.Error("The license have expired and can no longer be used");
			CurrentLicense = new LicensingStatus
			{
				Status = "AGPL - Open Source",
				Error = true,
				Message = "License expired"
			};
		}

		public void Dispose()
		{
			if (timer != null)
				timer.Dispose();
		}
	}
}
