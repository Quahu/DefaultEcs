@ECHO off

DEL test-*.xml
DEL *.nupkg
dotnet clean source\DefaultEcs.sln -c Release

cd source\DefaultEcs.Test

dotnet xunit -configuration Release -xml ..\..\test.xml -fxversion 2.1.0

cd ..\..

IF %ERRORLEVEL% GTR 0 GOTO :end

DEL test-*.xml

dotnet pack source\DefaultEcs\DefaultEcs.csproj -c Release -o ..\..\

:end