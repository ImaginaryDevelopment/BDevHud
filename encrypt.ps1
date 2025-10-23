# AES Encryption Script with Password and Salt
# Encrypts a secret value using AES-256 with a password
# Designed to run in GitHub Actions with secrets
# Usage: .\encrypt.ps1 -SecretValue "your-secret-here" [-Password <pwd>] [-SaltString <salt>]
#        Or set environment variables: ENCRYPTION_PASSWORD and ENCRYPTION_SALT

param(
    [Parameter(Mandatory=$true)]
    [string]$SecretValue,
    
    [Parameter(Mandatory=$false)]
    [string]$Password = $env:ENCRYPTION_PASSWORD,
    
    [Parameter(Mandatory=$false)]
    [string]$SaltString = $env:ENCRYPTION_SALT
)

# Validate that password and salt are provided either as parameters or environment variables
if ([string]::IsNullOrWhiteSpace($Password)) {
    throw "Password must be provided via -Password parameter or ENCRYPTION_PASSWORD environment variable"
}

if ([string]::IsNullOrWhiteSpace($SaltString)) {
    throw "Salt must be provided via -SaltString parameter or ENCRYPTION_SALT environment variable"
}

function Encrypt-WithAES {
    param(
        [string]$PlainText,
        [string]$Password,
        [string]$Salt
    )
    
    # Convert salt to bytes
    $saltBytes = [System.Text.Encoding]::UTF8.GetBytes($Salt)
    
    # Derive a key from the password using PBKDF2
    $rfc2898 = [System.Security.Cryptography.Rfc2898DeriveBytes]::new($Password, $saltBytes, 10000, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $key = $rfc2898.GetBytes(32) # 256-bit key for AES-256
    $iv = $rfc2898.GetBytes(16)  # 128-bit IV
    
    # Create AES encryptor
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $key
    $aes.IV = $iv
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    
    # Create encryptor
    $encryptor = $aes.CreateEncryptor()
    
    # Convert plaintext to bytes
    $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($PlainText)
    
    # Encrypt
    $encryptedBytes = $encryptor.TransformFinalBlock($plainBytes, 0, $plainBytes.Length)
    
    # Convert to Base64
    $encryptedBase64 = [Convert]::ToBase64String($encryptedBytes)
    
    # Cleanup
    $encryptor.Dispose()
    $aes.Dispose()
    
    return $encryptedBase64
}

# Main execution
try {
    Write-Host "`n🔐 AES Encryption Tool" -ForegroundColor Cyan
    Write-Host "=" * 50 -ForegroundColor Cyan
    
    $encrypted = Encrypt-WithAES -PlainText $SecretValue -Password $Password -Salt $SaltString
    
    Write-Host "`n✅ Encryption successful!" -ForegroundColor Green
    Write-Host "`n📋 Encrypted value (Base64):" -ForegroundColor Yellow
    Write-Host $encrypted -ForegroundColor White
    
    # Also save to clipboard if available
    try {
        Set-Clipboard -Value $encrypted
        Write-Host "`n✅ Encrypted value copied to clipboard!" -ForegroundColor Green
    } catch {
        # Clipboard not available, skip
    }
    
    Write-Host "`n💡 To decrypt, use: .\decrypt.ps1 -EncryptedValue `"$encrypted`" -Password `"$Password`" -SaltString `"$SaltString`"" -ForegroundColor Cyan
    Write-Host "=" * 50 -ForegroundColor Cyan
    
} catch {
    Write-Host "`n❌ Encryption failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
