//
// WindowsSecureMimeContext.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2013 Jeffrey Stedfast
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509.Store;

using RealCmsSigner = System.Security.Cryptography.Pkcs.CmsSigner;
using RealCmsRecipient = System.Security.Cryptography.Pkcs.CmsRecipient;
using RealCmsRecipientCollection = System.Security.Cryptography.Pkcs.CmsRecipientCollection;

using MimeKit.IO;

namespace MimeKit.Cryptography {
	/// <summary>
	/// An S/MIME cryptography context that uses Windows' <see cref="System.Security.Cryptography.X509Certificates.X509Store"/>.
	/// </summary>
	public class WindowsSecureMimeContext : SecureMimeContext
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="MimeKit.Cryptography.WindowsSecureMimeContext"/> class.
		/// </summary>
		/// <param name="location">The X.509 store location.</param>
		public WindowsSecureMimeContext (StoreLocation location)
		{
			StoreLocation = location;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="MimeKit.Cryptography.WindowsSecureMimeContext"/> class.
		/// </summary>
		public WindowsSecureMimeContext () : this (StoreLocation.CurrentUser)
		{
		}

		/// <summary>
		/// Gets the X.509 store location.
		/// </summary>
		/// <value>The store location.</value>
		public StoreLocation StoreLocation {
			get; private set;
		}

		#region implemented abstract members of SecureMimeContext

		/// <summary>
		/// Gets the X.509 certificate based on the selector.
		/// </summary>
		/// <returns>The certificate on success; otherwise <c>null</c>.</returns>
		/// <param name="selector">The search criteria for the certificate.</param>
		protected override Org.BouncyCastle.X509.X509Certificate GetCertificate (IX509Selector selector)
		{
			var storeNames = new StoreName[] { StoreName.My, StoreName.AddressBook, StoreName.TrustedPeople, StoreName.Root };

			foreach (var storeName in storeNames) {
				var store = new X509Store (storeName, StoreLocation);

				store.Open (OpenFlags.ReadOnly);

				try {
					foreach (var certificate in store.Certificates) {
						var cert = DotNetUtilities.FromX509Certificate (certificate);
						if (selector.Match (cert))
							return cert;
					}
				} finally {
					store.Close ();
				}
			}

			return null;
		}

		/// <summary>
		/// Gets the private key based on the provided selector.
		/// </summary>
		/// <returns>The private key on success; otherwise <c>null</c>.</returns>
		/// <param name="selector">The search criteria for the private key.</param>
		protected override AsymmetricKeyParameter GetPrivateKey (IX509Selector selector)
		{
			var store = new X509Store (StoreName.My, StoreLocation);

			store.Open (OpenFlags.ReadOnly);

			try {
				foreach (var certificate in store.Certificates) {
					if (!certificate.HasPrivateKey)
						continue;

					var cert = DotNetUtilities.FromX509Certificate (certificate);

					if (selector.Match (cert)) {
						var pair = DotNetUtilities.GetKeyPair (certificate.PrivateKey);
						return pair.Private;
					}
				}
			} finally {
				store.Close ();
			}

			return null;
		}

		/// <summary>
		/// Gets the trusted anchors.
		/// </summary>
		/// <returns>The trusted anchors.</returns>
		protected override Org.BouncyCastle.Utilities.Collections.HashSet GetTrustedAnchors ()
		{
			var storeNames = new StoreName[] { StoreName.TrustedPeople, StoreName.Root };
			var anchors = new Org.BouncyCastle.Utilities.Collections.HashSet ();

			foreach (var storeName in storeNames) {
				var store = new X509Store (storeName, StoreLocation);

				store.Open (OpenFlags.ReadOnly);

				foreach (var certificate in store.Certificates) {
					var cert = DotNetUtilities.FromX509Certificate (certificate);
					anchors.Add (new TrustAnchor (cert, null));
				}

				store.Close ();
			}

			return anchors;
		}

		/// <summary>
		/// Gets the intermediate certificates.
		/// </summary>
		/// <returns>The intermediate certificates.</returns>
		protected override IX509Store GetIntermediateCertificates ()
		{
			var storeNames = new StoreName[] { StoreName.My, StoreName.AddressBook, StoreName.TrustedPeople, StoreName.Root };
			var intermediate = new X509CertificateStore ();

			foreach (var storeName in storeNames) {
				var store = new X509Store (storeName, StoreLocation);

				store.Open (OpenFlags.ReadOnly);

				foreach (var certificate in store.Certificates) {
					var cert = DotNetUtilities.FromX509Certificate (certificate);
					intermediate.Add (cert);
				}

				store.Close ();
			}

			return intermediate;
		}

		/// <summary>
		/// Gets the certificate revocation lists.
		/// </summary>
		/// <returns>The certificate revocation lists.</returns>
		protected override IX509Store GetCertificateRevocationLists ()
		{
			var crls = new List<X509Crl> ();

			return X509StoreFactory.Create ("Crl/Collection", new X509CollectionStoreParameters (crls));
		}

		/// <summary>
		/// Gets the X509 certificate associated with the <see cref="MimeKit.MailboxAddress"/>.
		/// </summary>
		/// <returns>The certificate.</returns>
		/// <param name="mailbox">The mailbox.</param>
		/// <exception cref="CertificateNotFoundException">
		/// A certificate for the specified <paramref name="mailbox"/> could not be found.
		/// </exception>
		protected override CmsRecipient GetCmsRecipient (MailboxAddress mailbox)
		{
			var store = new X509Store (StoreName.My, StoreLocation);

			store.Open (OpenFlags.ReadOnly);

			try {
				foreach (var certificate in store.Certificates) {
					if (certificate.GetNameInfo (X509NameType.EmailName, false) != mailbox.Address)
						continue;

					var cert = DotNetUtilities.FromX509Certificate (certificate);

					return new CmsRecipient (cert);
				}
			} finally {
				store.Close ();
			}

			throw new CertificateNotFoundException (mailbox, "A valid certificate could not be found.");
		}

		RealCmsRecipient GetRealCmsRecipient (MailboxAddress mailbox)
		{
			var storeNames = new StoreName[] { StoreName.My, StoreName.AddressBook };

			foreach (var storeName in storeNames) {
				var store = new X509Store (storeName, StoreLocation);

				store.Open (OpenFlags.ReadOnly);

				try {
					foreach (var certificate in store.Certificates) {
						if (!certificate.HasPrivateKey)
							continue;

						if (certificate.GetNameInfo (X509NameType.EmailName, false) == mailbox.Address)
							return new RealCmsRecipient (certificate);
					}
				} finally {
					store.Close ();
				}
			}

			throw new CertificateNotFoundException (mailbox, "A valid certificate could not be found.");
		}

		RealCmsRecipientCollection GetRealCmsRecipients (IEnumerable<MailboxAddress> mailboxes)
		{
			var recipients = new RealCmsRecipientCollection ();

			foreach (var mailbox in mailboxes)
				recipients.Add (GetRealCmsRecipient (mailbox));

			return recipients;
		}

		/// <summary>
		/// Gets the cms signer for the specified <see cref="MimeKit.MailboxAddress"/>.
		/// </summary>
		/// <returns>The cms signer.</returns>
		/// <param name="mailbox">The mailbox.</param>
		/// <param name="digestAlgo">The preferred digest algorithm.</param>
		/// <exception cref="CertificateNotFoundException">
		/// A certificate for the specified <paramref name="mailbox"/> could not be found.
		/// </exception>
		protected override CmsSigner GetCmsSigner (MailboxAddress mailbox, DigestAlgorithm digestAlgo)
		{
			var store = new X509Store (StoreName.My, StoreLocation);

			store.Open (OpenFlags.ReadOnly);

			try {
				foreach (var certificate in store.Certificates) {
					if (!certificate.HasPrivateKey)
						continue;

					if (certificate.GetNameInfo (X509NameType.EmailName, false) != mailbox.Address)
						continue;

					var pair = DotNetUtilities.GetKeyPair (certificate.PrivateKey);
					var cert = DotNetUtilities.FromX509Certificate (certificate);
					var signer = new CmsSigner (cert, pair.Private);
					signer.DigestAlgorithm = digestAlgo;

					return signer;
				}
			} finally {
				store.Close ();
			}

			throw new CertificateNotFoundException (mailbox, "A valid signing certificate could not be found.");
		}

		RealCmsSigner GetRealCmsSigner (MailboxAddress mailbox, DigestAlgorithm digestAlgo)
		{
			var store = new X509Store (StoreName.My, StoreLocation);

			store.Open (OpenFlags.ReadOnly);

			try {
				foreach (var certificate in store.Certificates) {
					if (!certificate.HasPrivateKey)
						continue;

					if (certificate.GetNameInfo (X509NameType.EmailName, false) != mailbox.Address)
						continue;

					var signer = new RealCmsSigner (certificate);
					signer.DigestAlgorithm = new Oid (GetDigestOid (digestAlgo));
					signer.IncludeOption = X509IncludeOption.ExcludeRoot;
					return signer;
				}
			} finally {
				store.Close ();
			}

			throw new CertificateNotFoundException (mailbox, "A valid signing certificate could not be found.");
		}

		byte[] ReadAllBytes (Stream stream)
		{
			if (stream is MemoryBlockStream)
				return ((MemoryBlockStream) stream).ToArray ();

			if (stream is MemoryStream)
				return ((MemoryStream) stream).ToArray ();

			using (var memory = new MemoryBlockStream ()) {
				stream.CopyTo (memory, 4096);
				return memory.ToArray ();
			}
		}

		/// <summary>
		/// Sign and encapsulate the content using the specified signer.
		/// </summary>
		/// <returns>A new <see cref="MimeKit.Cryptography.ApplicationPkcs7Mime"/> instance
		/// containing the detached signature data.</returns>
		/// <param name="signer">The signer.</param>
		/// <param name="digestAlgo">The digest algorithm to use for signing.</param>
		/// <param name="content">The content.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="signer"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="content"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="digestAlgo"/> is out of range.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The specified <see cref="DigestAlgorithm"/> is not supported by this context.
		/// </exception>
		/// <exception cref="CertificateNotFoundException">
		/// A signing certificate could not be found for <paramref name="signer"/>.
		/// </exception>
		/// <exception cref="System.Security.Cryptography.CryptographicException">
		/// An error occurred in the cryptographic message syntax subsystem.
		/// </exception>
		public override ApplicationPkcs7Mime EncapsulatedSign (MailboxAddress signer, DigestAlgorithm digestAlgo, Stream content)
		{
			if (signer == null)
				throw new ArgumentNullException ("signer");

			if (content == null)
				throw new ArgumentNullException ("content");

			var contentInfo = new ContentInfo (ReadAllBytes (content));
			var cmsSigner = GetRealCmsSigner (signer, digestAlgo);
			var signed = new SignedCms (contentInfo, false);
			signed.ComputeSignature (cmsSigner);
			var signedData = signed.Encode ();

			return new ApplicationPkcs7Mime (SecureMimeType.SignedData, new MemoryStream (signedData, false));
		}

		/// <summary>
		/// Sign the content using the specified signer.
		/// </summary>
		/// <returns>A new <see cref="MimeKit.MimePart"/> instance
		/// containing the detached signature data.</returns>
		/// <param name="signer">The signer.</param>
		/// <param name="digestAlgo">The digest algorithm to use for signing.</param>
		/// <param name="content">The content.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="signer"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="content"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="digestAlgo"/> is out of range.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The specified <see cref="DigestAlgorithm"/> is not supported by this context.
		/// </exception>
		/// <exception cref="CertificateNotFoundException">
		/// A signing certificate could not be found for <paramref name="signer"/>.
		/// </exception>
		/// <exception cref="System.Security.Cryptography.CryptographicException">
		/// An error occurred in the cryptographic message syntax subsystem.
		/// </exception>
		public override MimePart Sign (MailboxAddress signer, DigestAlgorithm digestAlgo, Stream content)
		{
			if (signer == null)
				throw new ArgumentNullException ("signer");

			if (content == null)
				throw new ArgumentNullException ("content");

			var contentInfo = new ContentInfo (ReadAllBytes (content));
			var cmsSigner = GetRealCmsSigner (signer, digestAlgo);
			var signed = new SignedCms (contentInfo, true);
			signed.ComputeSignature (cmsSigner);
			var signedData = signed.Encode ();

			return new ApplicationPkcs7Signature (new MemoryStream (signedData, false));
		}

		/// <summary>
		/// Decrypt the encrypted data.
		/// </summary>
		/// <returns>The decrypted <see cref="MimeKit.MimeEntity"/>.</returns>
		/// <param name="encryptedData">The encrypted data.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="encryptedData"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.Security.Cryptography.CryptographicException">
		/// An error occurred in the cryptographic message syntax subsystem.
		/// </exception>
		public override MimeEntity Decrypt (Stream encryptedData)
		{
			if (encryptedData == null)
				throw new ArgumentNullException ("encryptedData");

			var enveloped = new EnvelopedCms ();
			enveloped.Decode (ReadAllBytes (encryptedData));

			var store = new X509Store (StoreName.My, StoreLocation);
			store.Open (OpenFlags.ReadOnly);

			enveloped.Decrypt ();
			store.Close ();

			var decryptedData = enveloped.Encode ();

			using (var memory = new MemoryStream (decryptedData, false)) {
				return MimeEntity.Load (memory);
			}
		}

		/// <summary>
		/// Import the specified certificate.
		/// </summary>
		/// <param name="certificate">The certificate.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="certificate"/> is <c>null</c>.
		/// </exception>
		public override void Import (Org.BouncyCastle.X509.X509Certificate certificate)
		{
			var store = new X509Store (StoreName.AddressBook, StoreLocation);

			store.Open (OpenFlags.ReadWrite);
			store.Add (new X509Certificate2 (certificate.GetEncoded ()));
			store.Close ();
		}

		/// <summary>
		/// Import the specified certificate revocation list.
		/// </summary>
		/// <param name="crl">The certificate revocation list.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="crl"/> is <c>null</c>.
		/// </exception>
		public override void Import (X509Crl crl)
		{
			if (crl == null)
				throw new ArgumentNullException ("crl");

			// FIXME: implement this
		}

		/// <summary>
		/// Imports certificates and keys from a pkcs12-encoded stream.
		/// </summary>
		/// <param name="stream">The raw certificate and key data.</param>
		/// <param name="password">The password to unlock the stream.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="stream"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="password"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// Importing keys is not supported by this cryptography context.
		/// </exception>
		public override void Import (Stream stream, string password)
		{
			if (stream == null)
				throw new ArgumentNullException ("stream");

			if (password == null)
				throw new ArgumentNullException ("password");

			byte[] rawData;

			if (stream is MemoryBlockStream) {
				rawData = ((MemoryBlockStream) stream).ToArray ();
			} else if (stream is MemoryStream) {
				rawData = ((MemoryStream) stream).ToArray ();
			} else {
				using (var memory = new MemoryStream ()) {
					stream.CopyTo (memory, 4096);
					rawData = memory.ToArray ();
				}
			}
			var store = new X509Store (StoreName.My, StoreLocation);
			var certs = new X509Certificate2Collection ();

			store.Open (OpenFlags.ReadWrite);
			certs.Import (rawData, password, X509KeyStorageFlags.UserKeySet);
			store.AddRange (certs);
			store.Close ();
		}

		#endregion
	}
}
