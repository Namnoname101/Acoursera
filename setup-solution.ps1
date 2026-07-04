# Script để tạo file Solution (.sln) và thêm các project
Set-Location -Path $PSScriptRoot

dotnet new sln -n AutomationPlatform -o src --format sln --force

Write-Host "Adding projects to Solution..."
dotnet sln src/AutomationPlatform.sln add src/AutomationPlatform.Domain/AutomationPlatform.Domain.csproj
dotnet sln src/AutomationPlatform.sln add src/AutomationPlatform.Application/AutomationPlatform.Application.csproj
dotnet sln src/AutomationPlatform.sln add src/AutomationPlatform.Infrastructure/AutomationPlatform.Infrastructure.csproj
dotnet sln src/AutomationPlatform.sln add src/AutomationPlatform.Presentation/AutomationPlatform.Presentation.csproj
dotnet sln src/AutomationPlatform.sln add src/AutomationPlatform.Tests/AutomationPlatform.Tests.csproj

Write-Host "Done! You can now open src/AutomationPlatform.sln in Visual Studio or build the project."
