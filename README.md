# Dynamics Account Processor

This project is an **Azure Function** that fetches `account` records from Microsoft Dynamics 365, updates a custom field `cr356_processed` to `TRUE` based on business logic, generates a CSV file with account data, and uploads the CSV to **Azure Blob Storage** for archival.

## Table of Contents

- [Project Structure](#project-structure)
- [How It Works](#how-it-works)
- [Environment Settings](#environment-settings)
- [Endpoints](#endpoints)
- [Technologies Used](#technologies-used)
- [How to Deploy](#how-to-deploy)
- [Known Issues](#known-issues)
- [Future Improvements](#future-improvements)

---

## Project Structure

| File/Folder                 | Purpose                                                   |
|------------------------------|-----------------------------------------------------------|
| `ProcessAccountsFunction.cs` | Main Azure Function for processing accounts               |
| `AccountRecord`              | Model representing Dynamics 365 account entity            |
| `README.md`                  | This documentation                                        |

---

## How It Works

1. **Authentication**: 
   - Authenticates against Azure AD using a client ID and client secret.
   - Gets an access token to call Microsoft Dynamics 365 Web API.

2. **Fetch Accounts**:
   - Fetches all `account` records from Dynamics 365 with selected fields.
   - Supports pagination using `@odata.nextLink`.

3. **Process Each Account**:
   - Checks if an account’s `cr356_duedate` is earlier than or equal to the current time and `cr356_processed` is "No".
   - Updates the `cr356_processed` field of eligible records to `"TRUE"` in Dynamics.

4. **CSV Generation**:
   - Writes all account records into a CSV file using `CsvHelper`.

5. **Blob Storage Upload**:
   - Uploads the generated CSV file to Azure Blob Storage under the path:
     ```
     accountentityprocessing/{yyyy/MM/dd}/AccountsProcessed_{HHmmss}.csv
     ```

6. **Logging**:
   - Logs important steps like data fetching, updates, errors, and file uploads.

---

## Environment Settings

All configurations are set inside the code:

| Setting Name         | Purpose                                         |
|-----------------------|-------------------------------------------------|
| `clientId`            | Azure AD App Registration Client ID            |
| `clientSecret`        | Azure AD App Registration Client Secret        |
| `tenantId`            | Azure Active Directory Tenant ID               |
| `dynamicsUrl`         | Dynamics 365 Instance URL                      |
| `blobConnectionString`| Azure Blob Storage connection string           |
| `blobContainerName`   | Blob Container where CSV will be uploaded       |

> **Important**: These sensitive values should ideally be moved to Azure Function App Settings (Environment Variables) in production.

---

## Endpoints

| Method | URL                          | Description                            |
|--------|-------------------------------|----------------------------------------|
| POST   | /api/ProcessAccountsHttp      | Triggers the account processing job    |

---

## Technologies Used

- **Azure Functions**
- **Microsoft.Identity.Client** (MSAL.NET for authentication)
- **Dynamics 365 Web API**
- **Azure Storage Blobs SDK**
- **CsvHelper** (for CSV generation)
- **Newtonsoft.Json** (for JSON parsing)

---

## How to Deploy

1. **Clone the Repository**:
   ```bash
   git clone https://your-repo-url.git
   cd DynamicsAccountProcessor
   ```

2. **Build Locally**:
   Make sure you have the Azure Functions Core Tools and .NET 6/7 installed.

   ```bash
   func start
   ```

3. **Deploy to Azure**:
   Use the Azure CLI or Visual Studio to deploy the Azure Function.

4. **Configure Application Settings**:
   Set the following settings in your Azure Function App:
   - `clientId`
   - `clientSecret`
   - `tenantId`
   - `dynamicsUrl`
   - `blobConnectionString`
   - `blobContainerName`

---

## Known Issues

- **Partial Updates**: Only a subset of records (e.g., 5478) get updated when processing large volumes (~138,000+ records).  
  > Investigating if throttling, timeout, or API limitations are involved. Implementing retries and batching is recommended.

- **Sensitive Info in Code**: Client secrets and keys are hardcoded — needs to be moved to environment variables for security.

---

## Future Improvements

- Implement **batch updates** instead of individual HTTP PATCH calls.
- Add **retry logic** for transient API failures and throttling.
- Move configuration settings to **Azure Key Vault** or **Application Settings**.
- Improve error handling and failure reporting (e.g., dead-letter records).
- Parallelize processing safely with throttling limits considered.
- Setup CI/CD pipelines for automatic deployment.

---

# License

This project is licensed under the MIT License.

---
