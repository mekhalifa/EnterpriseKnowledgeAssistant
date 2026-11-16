<#
.SYNOPSIS
  Gets an access token for a LOCAL DEVELOPMENT user from the Keycloak realm in docker-compose.

.DESCRIPTION
  Uses the password grant on the public client eka-cli, which the local realm enables for
  scripts and curl only. A deployed realm disables it (authorization code + PKCE instead).

  Users (password "local-dev-only"):
    alice  tenant A (11111111-...)          no groups
    carol  tenant A                         group hr
    dana   tenant A                         group finance
    root   tenant A                         role eka.admin (manages documents; reads none it is not granted)
    bruno  tenant B (22222222-...)          no groups

.EXAMPLE
  $token = ./deploy/keycloak/get-token.ps1 -User carol
  curl http://127.0.0.1:5000/api/documents -H "Authorization: Bearer $token"
#>
param(
    [Parameter(Mandatory)] [ValidateSet('alice', 'carol', 'dana', 'root', 'bruno')] [string] $User,
    [string] $Password = 'local-dev-only',
    [string] $Authority = 'http://127.0.0.1:8080/realms/eka'
)

$response = Invoke-RestMethod "$Authority/protocol/openid-connect/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
    grant_type = 'password'
    client_id  = 'eka-cli'
    username   = $User
    password   = $Password
    scope      = 'openid'
}

$response.access_token
