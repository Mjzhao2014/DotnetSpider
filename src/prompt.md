Feature Request: Robots.txt Middleware

Description
Implement a middleware, UseRobotsTxt, that enables spiders to optionally respect the robots.txt rules of target websites.

Details
1.  On spider startup, fetch the /robots.txt file for each target host using the existing HTTP client.
2. Allow enabling respecting robots.txt behavior via configuration (UseRobotsTxt).
3. Parse and interpret robots.txt according to following rules:
     - It consists of groups of rules starting with one or more User-agent: lines followed by directives such as Disallow, Allow and Crawl-delay
    - Matching is case-insensitive and uses prefix matching; the most specific match for the crawler’s user-agent string applies
    - If no matching group is found, and no User-agent: * group exists, crawling is allowed.
4. Interpret each directive as follows:
   - User-agent — Specifies which crawler(s) the following rules apply to, based on the crawler’s HTTP User-Agent header. Can be a specific bot name (e.g., Googlebot) or * to match all crawlers. Matching is by case-insensitive prefix.
   - Disallow — Lists URL path prefixes or patterns that the crawler must not request. Example:
        - Disallow: /private/ means do not fetch anything under /private/.
        - Disallow: *.pdf → block all PDF files.
   - Allow — Lists URL path prefixes that are permitted, even if a broader Disallow would otherwise block them. Example: Allow: /private/public-info/ lets the crawler access this path even if /private/ is disallowed.
   - Crawl-delay — If present, indicates the number of seconds the crawler should wait between consecutive requests to the same host
5. Determine if your user-agent matches any User-agent: rules, and apply only the rules from the most specific matching group.
6. Apply Allow and Disallow directives to decide which URLs you can fetch
7. If a Crawl-delay directive is present, enforce that delay between requests.
8. If the file is missing or cannot be retrieved, assume full access is allowed.

You don't need to generate new test cases.
