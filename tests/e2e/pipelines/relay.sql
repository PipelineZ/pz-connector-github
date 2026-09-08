INSERT INTO {{ sink('out', 'open_issues_out') }}
select id, number, title, state
from {{ source('gh', 'open_issues') }}
order by id
