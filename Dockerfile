# escape=`
FROM mcr.microsoft.com/dotnet/framework/aspnet:4.8-windowsservercore-ltsc2022
SHELL ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"]
ENV PYTHON_VERSION=3.14.8
COPY iis-source C:/downloads/gallery/iis-source
COPY iis-helper C:/downloads/gallery/iis-helper
COPY iis-site C:/downloads/gallery/iis-site
COPY requirements-worker.txt C:/downloads/gallery/requirements-worker.txt
COPY container C:/container
RUN & C:/container/Build-Image.ps1
EXPOSE 80
ENTRYPOINT ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\container\\Start-Gallery.ps1"]

