FROM python:3.13-slim
WORKDIR /app
COPY server.py auth.py archive_admin.py index.html style.css app.js Twemoji.zip Twemoji-NOTICE.txt ./
USER 65534:65534
EXPOSE 8080
ENTRYPOINT ["python", "server.py", "--host", "0.0.0.0", "--archive", "/archive"]
