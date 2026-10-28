-- Every comparing sign's hits and near misses with how alike they were, and what the admins decided about the member
-- afterwards (bad: removed or muted within 30 days; fine: found fine or an action undone). No ids, names or text.
-- Run read-only on the quasi box:  enf-sql prod --csv -f tools/tuning/export.sql > /workspace/scratch/similarity.csv
-- (Detection step 13; tools/tuning/tune.py reads the result.)
WITH compared AS (
    SELECT d.sign, 'hit' AS kind, d.similarity, d.group_id, d.account_id, d.detected_at,
           d.outcome IN ('ban', 'kick', 'tempban', 'mute', 'tempmute', 'delete') AS bad,
           d.outcome IN ('fine', 'undone') AS fine
    FROM detections d
    WHERE d.similarity IS NOT NULL AND d.mode IN ('live', 'observe') AND d.detected_at >= now() - interval '180 days'
    UNION ALL
    SELECT n.sign, 'near', n.similarity, n.group_id, n.account_id, n.detected_at,
           EXISTS (SELECT 1 FROM member_labels l WHERE l.group_id = n.group_id AND l.account_id = n.account_id AND l.label = 'bad'
                     AND l.undone_at IS NULL AND l.action IN ('ban', 'kick', 'tempban', 'mute', 'tempmute', 'delete')
                     AND l.decided_at >= n.detected_at AND l.decided_at < n.detected_at + interval '30 days'),
           EXISTS (SELECT 1 FROM member_labels l WHERE l.group_id = n.group_id AND l.account_id = n.account_id
                     AND (l.action = 'fine' OR l.undone_at IS NOT NULL)
                     AND l.decided_at >= n.detected_at AND l.decided_at < n.detected_at + interval '30 days')
    FROM detection_near_misses n
    WHERE n.account_id IS NOT NULL AND n.detected_at >= now() - interval '180 days'
)
SELECT c.sign, c.kind, round(c.similarity::numeric, 3) AS similarity, c.bad, c.fine AND NOT c.bad AS fine,
       CASE WHEN g.observed_user_count < 200 THEN 'small' WHEN g.observed_user_count < 2000 THEN 'medium' ELSE 'large' END AS group_size
FROM compared c JOIN groups g ON g.group_id = c.group_id;
