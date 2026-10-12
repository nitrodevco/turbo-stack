# An SFTP server standing in for a storage box (backup target). Key-only login for the "backup" user; a tiny HTTP
# admin API lets the tests install the panel's public key and read the host key (for comparing fingerprints).
FROM alpine:3.22

RUN apk add --no-cache bash openssh busybox-extras \
    && adduser -D -h /home/backup -s /bin/sh backup \
    && sed -i 's/^backup:!/backup:*/' /etc/shadow \
    && mkdir -p /home/backup/.ssh /www/cgi-bin \
    && touch /home/backup/.ssh/authorized_keys \
    && chown -R backup:backup /home/backup && chmod 700 /home/backup/.ssh && chmod 600 /home/backup/.ssh/authorized_keys \
    && ssh-keygen -A

COPY sshd_config /etc/ssh/sshd_config
COPY admin /www/cgi-bin/admin
RUN chmod 755 /www/cgi-bin/admin

EXPOSE 22 8081
ENTRYPOINT ["/bin/sh", "-c", "httpd -p 8081 -h /www && exec /usr/sbin/sshd -D -e"]
