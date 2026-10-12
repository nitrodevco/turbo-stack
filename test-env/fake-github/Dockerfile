# Fake GitHub for the Docker test environment: an SSH git server where each repository only
# accepts its own deploy keys (like GitHub), plus a tiny HTTP admin API used by the tests in
# place of clicking around on github.com (add a deploy key, push a commit).
FROM alpine:3.22

RUN apk add --no-cache bash git openssh busybox-extras \
    && adduser -D -h /home/git -s /bin/bash git \
    && sed -i 's/^git:!/git:*/' /etc/shadow \
    && mkdir -p /home/git/.ssh /www/cgi-bin \
    && touch /home/git/.ssh/authorized_keys \
    && chown -R git:git /home/git/.ssh && chmod 700 /home/git/.ssh && chmod 600 /home/git/.ssh/authorized_keys \
    && ssh-keygen -A \
    # The admin CGI runs as root on repositories owned by git.
    && git config --system safe.directory "*"

COPY sshd_config /etc/ssh/sshd_config
COPY git-only add-deploy-key commit-change seed-repos entrypoint /usr/local/bin/
COPY cgi-bin/ /www/cgi-bin/
COPY fixtures/ /fixtures/
RUN chmod 755 /usr/local/bin/* /www/cgi-bin/*

EXPOSE 22 8081
ENTRYPOINT ["/usr/local/bin/entrypoint"]
